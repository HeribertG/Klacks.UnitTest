// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins how the third stage (Holistic Harmonizer) ends the AutoWizard chain: skipped before it starts when readiness
/// says "not ready"; any failure of the stage falls back to the Harmonizer scenario (stage 3 only polishes), naming a
/// missing prerequisite when the re-check finds one and the failure otherwise; the outcome note lands on the kept
/// Harmonizer scenario while only the intermediates are deleted; and in the default deterministic mode (no
/// WIZARD3_MODE, no model) stage 3 runs.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.DTOs.Schedules.AutoWizard;
using Klacks.Api.Application.DTOs.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.DTOs.Schedules.Wizard;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Interfaces.Schedules.AutoWizard;
using Klacks.Api.Application.Interfaces.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.AutoWizard;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.Services.Schedules.PlanningRules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Services.Schedules.AutoWizard;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Klacks.Api.Domain.Interfaces.Settings;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.AutoWizard;

[TestFixture]
public class AutoWizardJobRunnerHolisticStageTests : AutoWizardJobRunnerTestHarness
{
    private const string TextOnlyReason = "The configured model 'text-model' cannot read the schedule image.";

    [Test]
    public async Task NotReadyBeforeStageThree_SkipsIt_AndCompletesWithTheHarmonizerScenario()
    {
        _readiness.CheckAsync(Arg.Any<CancellationToken>()).Returns(HolisticHarmonizerReadiness.NotReady(TextOnlyReason));

        var (jobId, state) = await RunChainAsync();

        await _holisticRunner.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        state.Result!.FinalScenarioId.ShouldBe(_harmonizerScenarioId);
        state.Result.HarmonizationSkipped.ShouldBeTrue();
        state.Result.HarmonizationSkippedReason.ShouldBe(TextOnlyReason);
        await _hubNotifier.Received(1).NotifyCompletedAsync(jobId, Arg.Is<AutoWizardJobResultDto>(d => d.HarmonizationSkipped));
        await _hubNotifier.DidNotReceiveWithAnyArgs().NotifyFailedAsync(default!);
    }

    [Test]
    public async Task StageThreeFails_AndReadinessNowSaysTextOnly_IsSkippedNotFailed()
    {
        _readiness.CheckAsync(Arg.Any<CancellationToken>())
            .Returns(HolisticHarmonizerReadiness.Ready(), HolisticHarmonizerReadiness.NotReady(TextOnlyReason));
        _holisticApply.ApplyAsScenarioAsync(
                _holisticStageJobId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns<(AnalyseScenarioResource, IReadOnlyList<Guid>, ScenarioComplianceReport?)>(
                _ => throw new InvalidOperationException("no holistic result cached"));

        var (_, state) = await RunChainAsync();

        await _holisticRunner.ReceivedWithAnyArgs(1).StartAsync(default!, default);
        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        state.Result!.FinalScenarioId.ShouldBe(_harmonizerScenarioId);
        state.Result.HarmonizationSkipped.ShouldBeTrue();
        state.Result.HarmonizationSkippedReason.ShouldBe(TextOnlyReason);
        await _hubNotifier.DidNotReceiveWithAnyArgs().NotifyFailedAsync(default!);
    }

    [Test]
    public async Task StageThreeFails_WhileThePrerequisiteIsStillReady_FallsBackToTheHarmonizerScenario()
    {
        // Stage 3 only polishes a complete plan: its failure must not throw away the finished stage-2 plan.
        _holisticApply.ApplyAsScenarioAsync(
                _holisticStageJobId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns<(AnalyseScenarioResource, IReadOnlyList<Guid>, ScenarioComplianceReport?)>(
                _ => throw new InvalidOperationException("no holistic result cached"));

        var (jobId, state) = await RunChainAsync();

        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        state.Result!.FinalScenarioId.ShouldBe(_harmonizerScenarioId);
        state.Result.HarmonizationSkipped.ShouldBeTrue();
        state.Result.HarmonizationSkippedReason.ShouldNotBeNull();
        state.Result.HarmonizationSkippedReason!.ShouldContain("Holistic Harmonizer stage did not produce a result.");
        await _hubNotifier.Received(1).NotifyCompletedAsync(jobId, Arg.Is<AutoWizardJobResultDto>(d => d.HarmonizationSkipped));
        await _hubNotifier.DidNotReceiveWithAnyArgs().NotifyFailedAsync(default!);
    }

    [Test]
    public async Task InvalidApprovedHardRule_DoesNotStopTheChain_AndIsReportedAsAWarning()
    {
        var invalidRuleId = Guid.NewGuid();
        _ruleSetLoader.LoadRuleSetAsync(default!, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new PlanningRuleSet([], [], [], [], [invalidRuleId]));

        var (_, state) = await RunChainAsync();

        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        state.Result!.FinalScenarioId.ShouldBe(_holisticScenarioId);
        state.Result.ComplianceViolations.ShouldBeEmpty();
        var warning = state.Result.PlanningRuleWarnings.ShouldNotBeNull().ShouldHaveSingleItem();
        warning.Comment.ShouldBe(ScheduleValidationKeys.PlanningRuleInvalid);
        warning.Type.ShouldBe(ScheduleValidationType.Warning);
        warning.CommentParams![PlanningRuleNotificationMapper.RuleIdParam].ShouldBe(invalidRuleId.ToString());
        await _ruleSetLoader.Received(1).LoadRuleSetAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), PeriodFrom, PeriodUntil, Arg.Any<Guid?>(), Arg.Any<int>(),
            PlanningRuleSources.PlanningConstraints, InvalidHardRuleHandling.Report, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InvalidApprovedHardRule_IsKeptApartFromTheWizardComplianceViolations()
    {
        var wizardViolation = new ScheduleValidationNotificationDto
        {
            Type = ScheduleValidationType.Warning,
            ClientId = Guid.NewGuid(),
            Date = PeriodFrom,
            Comment = ScheduleValidationKeys.PlanningRule,
        };
        _wizardApply.ApplyAsScenarioAsync(
                _wizardStageJobId, Arg.Any<Guid?>(), false, Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), Arg.Any<string?>())
            .Returns((Resource(_wizardScenarioId), new WizardApplyOutcome([], [wizardViolation], [], false)));
        var invalidRuleIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        _ruleSetLoader.LoadRuleSetAsync(default!, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new PlanningRuleSet([], [], [], [], invalidRuleIds));

        var (jobId, state) = await RunChainAsync();

        var violation = state.Result!.ComplianceViolations.ShouldHaveSingleItem();
        violation.Comment.ShouldBe(wizardViolation.Comment);
        violation.ClientId.ShouldBe(wizardViolation.ClientId);
        state.Result.PlanningRuleWarnings.ShouldNotBeNull().Count.ShouldBe(invalidRuleIds.Length);
        state.Result.PlanningRuleWarnings!.ShouldAllBe(w => w.Comment == ScheduleValidationKeys.PlanningRuleInvalid);
        await _hubNotifier.Received(1).NotifyCompletedAsync(jobId, Arg.Is<AutoWizardJobResultDto>(d =>
            d.ComplianceViolations.Count == 1 && d.PlanningRuleWarnings!.Count == invalidRuleIds.Length));
    }

    [Test]
    public async Task NoInvalidRule_ReportsNoPlanningRuleWarning()
    {
        var (_, state) = await RunChainAsync();

        state.Result!.PlanningRuleWarnings.ShouldNotBeNull().ShouldBeEmpty();
    }

    [Test]
    public async Task DeterministicDefaultMode_WithoutAnyModel_RunsStageThree()
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsEntity?)null);
        RebuildRunner(new HolisticHarmonizerReadinessCheck(settingsReader, new HolisticHarmonizerModelCapabilityCache()));

        var (_, state) = await RunChainAsync();

        await _holisticRunner.ReceivedWithAnyArgs(1).StartAsync(default!, default);
        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        state.Result!.FinalScenarioId.ShouldBe(_holisticScenarioId);
        state.Result.HarmonizationSkipped.ShouldBeFalse();
    }

    [Test]
    public async Task SkippedStageThree_NotesTheOutcomeOnTheHarmonizerScenario_AndDeletesOnlyTheIntermediate()
    {
        _readiness.CheckAsync(Arg.Any<CancellationToken>()).Returns(HolisticHarmonizerReadiness.NotReady(TextOnlyReason));

        await RunChainAsync();

        await _scenarioRepository.Received(1).Put(Arg.Is<AnalyseScenario>(s => s.Id == _harmonizerScenarioId));
        await _scenarioRepository.DidNotReceive().Put(Arg.Is<AnalyseScenario>(s => s.Id != _harmonizerScenarioId));
        _scenarios[_harmonizerScenarioId].Description.ShouldBe(
            "harmonized | " + AutoWizardStageOutcomePlanner.BuildHarmonizationSkippedNote(TextOnlyReason));
        DeletedScenarioIds().ShouldBe(new[] { _wizardScenarioId });
    }

    [Test]
    public async Task FailedStageThree_NotesTheFallbackOnTheHarmonizerScenario_AndDeletesOnlyTheIntermediate()
    {
        _holisticApply.ApplyAsScenarioAsync(
                _holisticStageJobId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns<(AnalyseScenarioResource, IReadOnlyList<Guid>, ScenarioComplianceReport?)>(
                _ => throw new InvalidOperationException("no holistic result cached"));

        var (_, state) = await RunChainAsync();

        await _scenarioRepository.Received(1).Put(Arg.Is<AnalyseScenario>(s => s.Id == _harmonizerScenarioId));
        _scenarios[_harmonizerScenarioId].Description.ShouldBe(
            "harmonized | " + AutoWizardStageOutcomePlanner.BuildHarmonizationSkippedNote(state.Result!.HarmonizationSkippedReason!));
        DeletedScenarioIds().ShouldBe(new[] { _wizardScenarioId });
    }

    [Test]
    public async Task EveryStageScenario_IsNamedWithItsOwnKind_InTheRequestLanguage()
    {
        await RunChainAsync();

        await _wizardApply.Received(1).ApplyAsScenarioAsync(
            _wizardStageJobId, Arg.Any<Guid?>(), false, Arg.Any<CancellationToken>(),
            ScenarioNameKind.AutoPlan, RequestLanguage);
        await _harmonizerApply.Received(1).ApplyAsScenarioAsync(
            _harmonizerStageJobId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>(),
            ScenarioNameKind.AutoHarmonizer, RequestLanguage, Arg.Any<bool>(), Arg.Any<bool>());
        await _holisticApply.Received(1).ApplyAsScenarioAsync(
            _holisticStageJobId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>(),
            ScenarioNameKind.Auto, RequestLanguage, Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Test]
    public async Task StageThreeRuns_CompletesWithTheHolisticScenario_WithoutANote()
    {
        var (_, state) = await RunChainAsync();

        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        state.Result!.FinalScenarioId.ShouldBe(_holisticScenarioId);
        state.Result.HarmonizationSkipped.ShouldBeFalse();
        await _scenarioRepository.DidNotReceiveWithAnyArgs().Put(default!);
        DeletedScenarioIds().ShouldBe(new[] { _wizardScenarioId, _harmonizerScenarioId }, ignoreOrder: true);
    }
}
