// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The AutoWizard's soft-keyword retry driven through the real AutoWizardJobRunner: the strict (Hard) chain stays the
/// final scenario; a second chain in Soft mode runs only when it was asked for, the strict scenario leaves slots open and
/// a restricting schedule command exists; its scenario is kept, marked Soft and offered as SoftAlternative only when it
/// fills more slots and breaks a restricting wish; a better soft scenario that breaks none is promoted to the final result
/// and the strict one is removed; every other outcome removes all soft scenarios without touching the strict result.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.DTOs.Schedules.AutoWizard;
using Klacks.Api.Application.DTOs.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.DTOs.Schedules.KeywordWishes;
using Klacks.Api.Application.DTOs.Schedules.Summary;
using Klacks.Api.Application.DTOs.Schedules.Wizard;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.Api.Domain.Enums;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.AutoWizard;

[TestFixture]
public sealed class AutoWizardJobRunnerSoftKeywordRetryTests : AutoWizardJobRunnerTestHarness
{
    private const int Demanded = 10;
    private const int StrictFilled = 8;
    private const string AgentName = "Anna Muster";

    private readonly Guid _softWizardStageJobId = Guid.NewGuid();
    private readonly Guid _softHarmonizerStageJobId = Guid.NewGuid();
    private readonly Guid _softHolisticStageJobId = Guid.NewGuid();
    private readonly Guid _softWizardScenarioId = Guid.NewGuid();
    private readonly Guid _softHarmonizerScenarioId = Guid.NewGuid();
    private readonly Guid _softHolisticScenarioId = Guid.NewGuid();
    private readonly Guid _strictFinalToken = Guid.NewGuid();
    private readonly Guid _softFinalToken = Guid.NewGuid();
    private readonly Guid _agentId = Guid.NewGuid();

    private IScenarioSummaryBuilder _summaryBuilder = null!;
    private IKeywordWishCountService _wishCounter = null!;

    [SetUp]
    public void SetUpSoftRetry()
    {
        _summaryBuilder = Substitute.For<IScenarioSummaryBuilder>();
        _wishCounter = Substitute.For<IKeywordWishCountService>();

        _wizardRunner.StartAsync(Arg.Is<WizardContextRequest>(r => r.KeywordMode == KeywordMode.Soft), Arg.Any<CancellationToken>())
            .Returns(_softWizardStageJobId);
        _harmonizerRunner.StartAsync(Arg.Is<HarmonizerContextRequest>(r => r.KeywordMode == KeywordMode.Soft), Arg.Any<CancellationToken>())
            .Returns(_softHarmonizerStageJobId);
        _holisticRunner.StartAsync(Arg.Is<HolisticHarmonizerRunInput>(r => r.KeywordMode == KeywordMode.Soft), Arg.Any<CancellationToken>())
            .Returns(_softHolisticStageJobId);

        _wizardResults.Store(_softWizardStageJobId, new CoreScenario { KeywordMode = KeywordMode.Soft }, analyseToken: null);
        var emptyBitmap = new HarmonyBitmap([], [], new Cell[0, 0]);
        _harmonizerResults.Store(_softHarmonizerStageJobId, emptyBitmap, emptyBitmap, sourceAnalyseToken: null);

        _scenarios[_softWizardScenarioId] = new() { Id = _softWizardScenarioId, Name = "Soft-Wizard" };
        _scenarios[_softHarmonizerScenarioId] = new() { Id = _softHarmonizerScenarioId, Name = "Soft-Harmonizer" };
        _scenarios[_softHolisticScenarioId] = new() { Id = _softHolisticScenarioId, Name = "Soft-Holistic" };

        _wizardApply.ApplyAsScenarioAsync(
                _softWizardStageJobId, Arg.Any<Guid?>(), false, Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), Arg.Any<string?>())
            .Returns((Resource(_softWizardScenarioId), new WizardApplyOutcome([], [], [], false)));
        _harmonizerApply.ApplyAsScenarioAsync(
                _softHarmonizerStageJobId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns((Resource(_softHarmonizerScenarioId), (IReadOnlyList<Guid>)[], (ScenarioComplianceReport?)null));
        _holisticApply.ApplyAsScenarioAsync(
                _holisticStageJobId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns((ResourceWithToken(_holisticScenarioId, _strictFinalToken), (IReadOnlyList<Guid>)[], (ScenarioComplianceReport?)null));
        _holisticApply.ApplyAsScenarioAsync(
                _softHolisticStageJobId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns((ResourceWithToken(_softHolisticScenarioId, _softFinalToken), (IReadOnlyList<Guid>)[], (ScenarioComplianceReport?)null));

        StubSummary(_strictFinalToken, StrictFilled);
        StubSummary(_softFinalToken, Demanded, new KeywordWishesDto(1, 0, 0, 0,
            [new KeywordWishItemDto(_agentId, PeriodFrom, "Free", "BrokenFree", AgentName)]));
        _wishCounter.HasRestrictingWishesAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), PeriodFrom, PeriodUntil, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    protected override void AddServices(ServiceCollection services)
    {
        services.AddSingleton<IScenarioSummaryBuilder>(_ => _summaryBuilder);
        services.AddSingleton<IKeywordWishCountService>(_ => _wishCounter);
    }

    [Test]
    public async Task SoftScenarioFillingMoreSlots_IsKeptAsAlternative_MarkedSoft_AndTheStrictOneStaysFinal()
    {
        var (_, state) = await RunChainAsync(SoftRetryRequest());

        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        var result = state.Result.ShouldNotBeNull();
        result.FinalScenarioId.ShouldBe(_holisticScenarioId);
        result.KeywordMode.ShouldBe(KeywordMode.Hard);
        result.SoftKeywordRetryOutcome.ShouldBe(AutoWizardSoftKeywordRetryOutcomes.Kept);
        var soft = result.SoftAlternative.ShouldNotBeNull();
        soft.ScenarioId.ShouldBe(_softHolisticScenarioId);
        soft.AdditionalFilledSlots.ShouldBe(Demanded - StrictFilled);
        soft.BrokenWishes.ShouldBe([new AutoWizardBrokenWishDto(_agentId, AgentName, PeriodFrom, "Free", "BrokenFree")]);

        _scenarios[_softHolisticScenarioId].SoftKeywordPlan.ShouldBeTrue();
        _scenarios[_holisticScenarioId].SoftKeywordPlan.ShouldBeFalse();
        _scenarios[_softHolisticScenarioId].Description.ShouldNotBeNull().ShouldContain("soft-keyword alternative");

        var deleted = DeletedScenarioIds();
        deleted.ShouldBe(
            [_wizardScenarioId, _harmonizerScenarioId, _softWizardScenarioId, _softHarmonizerScenarioId],
            ignoreOrder: true);

        await _wizardRunner.Received(1).StartAsync(
            Arg.Is<WizardContextRequest>(r => r.KeywordMode == KeywordMode.Soft), Arg.Any<CancellationToken>());
        await _harmonizerRunner.Received(1).StartAsync(
            Arg.Is<HarmonizerContextRequest>(r => r.KeywordMode == KeywordMode.Soft), Arg.Any<CancellationToken>());
        await _holisticRunner.Received(1).StartAsync(
            Arg.Is<HolisticHarmonizerRunInput>(r => r.KeywordMode == KeywordMode.Soft), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SoftScenarioFillingMoreSlotsWithoutBreakingAnyRestriction_IsPromoted_AndTheStrictOneIsRemoved()
    {
        StubSummary(_softFinalToken, Demanded, new KeywordWishesDto(0, 0, 0, 2,
        [
            new KeywordWishItemDto(_agentId, PeriodFrom, "OnlyNight", "Unfulfilled", AgentName),
            new KeywordWishItemDto(_agentId, PeriodUntil, "OnlyNight", "Unfulfilled", AgentName),
        ]));

        var (_, state) = await RunChainAsync(SoftRetryRequest());

        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        var result = state.Result.ShouldNotBeNull();
        result.SoftKeywordRetryOutcome.ShouldBe(AutoWizardSoftKeywordRetryOutcomes.Promoted);
        result.FinalScenarioId.ShouldBe(_softHolisticScenarioId);
        result.FinalScenarioToken.ShouldBe(_softFinalToken);
        result.KeywordMode.ShouldBe(KeywordMode.Hard);
        result.SoftAlternative.ShouldBeNull();
        result.Summary.ShouldNotBeNull().FilledSlots.ShouldBe(Demanded);

        _scenarios[_softHolisticScenarioId].SoftKeywordPlan.ShouldBeFalse();
        _scenarios[_softHolisticScenarioId].Description.ShouldNotBeNull().ShouldContain("without breaking any schedule-command wish");
        DeletedScenarioIds().ShouldBe(
            [_wizardScenarioId, _harmonizerScenarioId, _holisticScenarioId, _softWizardScenarioId, _softHarmonizerScenarioId],
            ignoreOrder: true);
    }

    [Test]
    public async Task PromotedScenarioWhoseReportsFail_IsRemoved_AndTheStrictOneStaysTheResultWithOutcomeFailed()
    {
        StubSummary(_softFinalToken, Demanded, new KeywordWishesDto(0, 0, 0, 0, []));
        _ruleSetLoader.LoadRuleSetAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(),
                Arg.Any<int>(), Arg.Any<PlanningRuleSources>(), Arg.Any<InvalidHardRuleHandling>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Guid?>(3) == _softFinalToken
                ? throw new InvalidOperationException("planning rules of the promoted scenario could not be read")
                : new PlanningRuleSet([], [], [], [], []));

        var (_, state) = await RunChainAsync(SoftRetryRequest());

        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        var result = state.Result.ShouldNotBeNull();
        result.SoftKeywordRetryOutcome.ShouldBe(AutoWizardSoftKeywordRetryOutcomes.Failed);
        result.FinalScenarioId.ShouldBe(_holisticScenarioId);
        result.FinalScenarioToken.ShouldBe(_strictFinalToken);
        result.SoftAlternative.ShouldBeNull();
        result.Summary.ShouldNotBeNull().FilledSlots.ShouldBe(StrictFilled);
        DeletedScenarioIds().ShouldBe(
            [_wizardScenarioId, _harmonizerScenarioId, _softWizardScenarioId, _softHarmonizerScenarioId, _softHolisticScenarioId],
            ignoreOrder: true);
    }

    [Test]
    public async Task SoftScenarioFillingNoMoreSlots_IsDiscardedCompletely()
    {
        StubSummary(_softFinalToken, StrictFilled);

        var (_, state) = await RunChainAsync(SoftRetryRequest());

        var result = state.Result.ShouldNotBeNull();
        result.FinalScenarioId.ShouldBe(_holisticScenarioId);
        result.SoftAlternative.ShouldBeNull();
        result.SoftKeywordRetryOutcome.ShouldBe(AutoWizardSoftKeywordRetryOutcomes.NotBetter);
        _scenarios[_softHolisticScenarioId].SoftKeywordPlan.ShouldBeFalse();
        DeletedScenarioIds().ShouldBe(
            [_wizardScenarioId, _harmonizerScenarioId, _softWizardScenarioId, _softHarmonizerScenarioId, _softHolisticScenarioId],
            ignoreOrder: true);
    }

    [Test]
    public async Task WithoutRestrictingCommands_NoSecondChainRuns()
    {
        _wishCounter.HasRestrictingWishesAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), PeriodFrom, PeriodUntil, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var (_, state) = await RunChainAsync(SoftRetryRequest());

        state.Result.ShouldNotBeNull().SoftKeywordRetryOutcome.ShouldBe(AutoWizardSoftKeywordRetryOutcomes.NotNeeded);
        await _wizardRunner.Received(1).StartAsync(Arg.Any<WizardContextRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WithoutOpenSlots_NoSecondChainRuns_AndTheCommandsAreNotEvenRead()
    {
        StubSummary(_strictFinalToken, Demanded);

        var (_, state) = await RunChainAsync(SoftRetryRequest());

        state.Result.ShouldNotBeNull().SoftKeywordRetryOutcome.ShouldBe(AutoWizardSoftKeywordRetryOutcomes.NotNeeded);
        await _wizardRunner.Received(1).StartAsync(Arg.Any<WizardContextRequest>(), Arg.Any<CancellationToken>());
        await _wishCounter.DidNotReceiveWithAnyArgs().HasRestrictingWishesAsync(default!, default, default, default, default);
    }

    [Test]
    public async Task NotRequested_NoSecondChainRuns()
    {
        var (_, state) = await RunChainAsync(Request(Guid.NewGuid()));

        state.Result.ShouldNotBeNull().SoftKeywordRetryOutcome.ShouldBe(AutoWizardSoftKeywordRetryOutcomes.NotRequested);
        await _wizardRunner.Received(1).StartAsync(Arg.Any<WizardContextRequest>(), Arg.Any<CancellationToken>());
        await _wishCounter.DidNotReceiveWithAnyArgs().HasRestrictingWishesAsync(default!, default, default, default, default);
    }

    [Test]
    public async Task FailingSoftChain_KeepsTheStrictResult_AndRemovesTheSoftScenarios()
    {
        _harmonizerApply.ApplyAsScenarioAsync(
                _softHarmonizerStageJobId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns<(AnalyseScenarioResource, IReadOnlyList<Guid>, ScenarioComplianceReport?)>(
                _ => throw new InvalidOperationException("soft harmonizer apply failed"));

        var (_, state) = await RunChainAsync(SoftRetryRequest());

        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        var result = state.Result.ShouldNotBeNull();
        result.FinalScenarioId.ShouldBe(_holisticScenarioId);
        result.SoftAlternative.ShouldBeNull();
        result.SoftKeywordRetryOutcome.ShouldBe(AutoWizardSoftKeywordRetryOutcomes.Failed);
        DeletedScenarioIds().ShouldBe([_wizardScenarioId, _harmonizerScenarioId, _softWizardScenarioId], ignoreOrder: true);
    }

    [Test]
    public async Task CancelDuringTheSoftChain_StillCompletesWithTheStrictResult()
    {
        var softStarted = new TaskCompletionSource();
        _wizardRunner.StartAsync(Arg.Is<WizardContextRequest>(r => r.KeywordMode == KeywordMode.Soft), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                softStarted.TrySetResult();
                return BlockUntilCancelledAsync(call.ArgAt<CancellationToken>(1));
            });

        var jobId = await _runner.StartAsync(SoftRetryRequest(), CancellationToken.None);
        await softStarted.Task.WaitAsync(ChainTimeout);
        _runner.TryCancel(jobId).ShouldBeTrue();
        var (_, state) = await WaitForChainAsync(jobId);

        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        var result = state.Result.ShouldNotBeNull();
        result.FinalScenarioId.ShouldBe(_holisticScenarioId);
        result.SoftAlternative.ShouldBeNull();
        result.SoftKeywordRetryOutcome.ShouldBe(AutoWizardSoftKeywordRetryOutcomes.Failed);
        DeletedScenarioIds().ShouldBe([_wizardScenarioId, _harmonizerScenarioId], ignoreOrder: true);
    }

    private static StartAutoWizardRequest SoftRetryRequest() => Request(Guid.NewGuid()) with { SoftKeywordRetry = true };

    private AnalyseScenarioResource ResourceWithToken(Guid scenarioId, Guid token)
    {
        var resource = Resource(scenarioId);
        resource.Token = token;
        return resource;
    }

    private void StubSummary(Guid token, int filled, KeywordWishesDto? wishes = null)
        => _summaryBuilder.BuildAsync(token, Arg.Any<CancellationToken>())
            .Returns(new ScenarioSummaryDto(token, PeriodFrom, PeriodUntil, 1, filled, Demanded, filled, [], [], KeywordWishes: wishes));
}
