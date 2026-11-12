// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the AutoWizard chain Wizard 1 -> Holistic Harmonizer (owner decision 2026-11-12: the Harmonizer, Wizard 2, is not
/// part of it): stage 3 starts from the Wizard 1 scenario and always runs; any failure of the stage falls back to the
/// Wizard 1 scenario (stage 3 only polishes) and names the failure; the outcome note lands on the kept Wizard 1 scenario
/// while only the intermediates are deleted.
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

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.AutoWizard;

[TestFixture]
public class AutoWizardJobRunnerHolisticStageTests : AutoWizardJobRunnerTestHarness
{
    [Test]
    public async Task Chain_SkipsTheHarmonizer_AndStageThreeStartsFromTheWizardScenario()
    {
        var wizardToken = Guid.NewGuid();
        var wizardScenario = Resource(_wizardScenarioId);
        wizardScenario.Token = wizardToken;
        _wizardApply.ApplyAsScenarioAsync(
                _wizardStageJobId, Arg.Any<Guid?>(), false, Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), Arg.Any<string?>())
            .Returns((wizardScenario, new WizardApplyOutcome([], [], [], false)));

        var (_, state) = await RunChainAsync();

        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        await _holisticRunner.Received(1).StartAsync(
            Arg.Is<HolisticHarmonizerRunInput>(r => r.AnalyseToken == wizardToken), Arg.Any<CancellationToken>());
        await _harmonizerApply.DidNotReceiveWithAnyArgs().ApplyAsScenarioAsync(default, default, default, default, default, default, default);
    }

    [Test]
    public async Task StageThreeFails_FallsBackToTheWizardScenario()
    {
        _holisticApply.ApplyAsScenarioAsync(
                _holisticStageJobId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns<(AnalyseScenarioResource, IReadOnlyList<Guid>, ScenarioComplianceReport?)>(
                _ => throw new InvalidOperationException("no holistic result cached"));

        var (jobId, state) = await RunChainAsync();

        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        state.Result!.FinalScenarioId.ShouldBe(_wizardScenarioId);
        state.Result.HarmonizationSkipped.ShouldBeTrue();
        state.Result.HarmonizationSkippedReason.ShouldBe(
            AutoWizardStageOutcomePlanner.HolisticStageFallbackReason("Holistic Harmonizer stage did not produce a result."));
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
    public async Task FailedStageThree_NotesTheFallbackOnTheWizardScenario_AndDeletesNothing()
    {
        _holisticApply.ApplyAsScenarioAsync(
                _holisticStageJobId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns<(AnalyseScenarioResource, IReadOnlyList<Guid>, ScenarioComplianceReport?)>(
                _ => throw new InvalidOperationException("no holistic result cached"));

        var (_, state) = await RunChainAsync();

        await _scenarioRepository.Received(1).Put(Arg.Is<AnalyseScenario>(s => s.Id == _wizardScenarioId));
        _scenarios[_wizardScenarioId].Description.ShouldBe(
            WizardScenarioDescription + " | " + AutoWizardStageOutcomePlanner.BuildHarmonizationSkippedNote(state.Result!.HarmonizationSkippedReason!));
        DeletedScenarioIds().ShouldBeEmpty();
    }

    [Test]
    public async Task EveryStageScenario_IsNamedWithItsOwnKind_InTheRequestLanguage()
    {
        await RunChainAsync();

        await _wizardApply.Received(1).ApplyAsScenarioAsync(
            _wizardStageJobId, Arg.Any<Guid?>(), false, Arg.Any<CancellationToken>(),
            ScenarioNameKind.AutoPlan, RequestLanguage);
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
        DeletedScenarioIds().ShouldBe(new[] { _wizardScenarioId });
    }
}
