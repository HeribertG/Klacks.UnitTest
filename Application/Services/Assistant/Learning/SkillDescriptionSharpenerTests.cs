// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the description gate. A description is the largest part of a skill's index text, so a change can
/// push a neighbouring skill out of reach; the only honest check is to apply it, replay, and put it back. In
/// Collect the optimizer is never asked for a proposal and nothing is measured, in Gate only a manually
/// triggered run measures and nothing stays live, in AutoApply a passing change stays. A change
/// passes only with a net gain over a paired replay and without a holdout regression, and the index has to
/// confirm both the change and the restore.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Learning;

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Application.Services.Assistant.Learning;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SkillDescriptionSharpenerTests
{
    private const string Before = "Lists everything about clients.";
    private const string After = "Lists the contract data of one client.";
    private const string Narrowed = "Lists the contract data of one client by client number.";
    private const string OtherSkill = "create_client";
    private const string Model = "deepseek-v4-pro";
    private const int OriginalVersion = 3;
    private const int RaisedMinimum = SkillLearningDefaults.MinGoldenCasesForAutoApply + 1;

    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly GoldsetItemRef TrainRef = new(TurnEvalDefaults.DefaultGoldset, "ts-train-1");
    private static readonly GoldsetItemRef HoldoutRef = new(TurnEvalDefaults.DefaultGoldset, "ts-042");

    private ISkillDescriptionOptimizer _optimizer = null!;
    private IProposedSkillChangeRepository _proposals = null!;
    private IAgentSkillRepository _skills = null!;
    private ISkillLearningGoldenCaseRepository _goldenCases = null!;
    private ISkillRoutingOracle _oracle = null!;
    private ISkillCatalogRefresher _refresher = null!;
    private ISkillLearningOptionsProvider _options = null!;
    private IGoldsetHoldoutReplayGate _holdoutGate = null!;
    private ISkillIndexStateVerifier _indexVerifier = null!;
    private ILogger<SkillDescriptionSharpener> _logger = null!;
    private SkillDescriptionSharpener _sharpener = null!;
    private AgentSkill _skill = null!;
    private List<(string Description, int Version)> _writtenSkills = null!;

    [SetUp]
    public void SetUp()
    {
        _optimizer = Substitute.For<ISkillDescriptionOptimizer>();
        _optimizer.GenerateProposalsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(SkillDescriptionOptimizerResult.Empty);
        _proposals = Substitute.For<IProposedSkillChangeRepository>();
        _skills = Substitute.For<IAgentSkillRepository>();
        _goldenCases = Substitute.For<ISkillLearningGoldenCaseRepository>();
        _goldenCases.ListHoldoutAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([]);
        _goldenCases.CountHoldoutAsync(Arg.Any<CancellationToken>()).Returns(SkillLearningDefaults.MinGoldenCasesForAutoApply);

        _oracle = Substitute.For<ISkillRoutingOracle>();
        _oracle.FindFailingGoldenCasesAsync(Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        _refresher = Substitute.For<ISkillCatalogRefresher>();

        _options = Substitute.For<ISkillLearningOptionsProvider>();
        GivenMode(SkillLearningMode.AutoApply);

        _skill = new AgentSkill { Id = Guid.NewGuid(), Name = "list_clients", Description = Before, Version = OriginalVersion };
        _skills.GetByIdAsync(_skill.Id, Arg.Any<CancellationToken>()).Returns(_skill);
        _writtenSkills = [];
        _skills.When(x => x.UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>()))
            .Do(call => _writtenSkills.Add((call.Arg<AgentSkill>().Description, call.Arg<AgentSkill>().Version)));

        _holdoutGate = Substitute.For<IGoldsetHoldoutReplayGate>();
        GivenPlan([], [TrainRef]);
        _holdoutGate.ReplayAsync(Arg.Any<GoldsetReplayPlan>(), Arg.Any<CancellationToken>())
            .Returns(_ => Verdicts((TrainRef, _skill.Description != Before)));

        _indexVerifier = Substitute.For<ISkillIndexStateVerifier>();
        _indexVerifier.IsIndexedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        _logger = Substitute.For<ILogger<SkillDescriptionSharpener>>();

        _sharpener = new SkillDescriptionSharpener(
            _optimizer, _proposals, _skills, _goldenCases, _oracle, _refresher, _options,
            _holdoutGate, _indexVerifier, _logger);
    }

    private void GivenMode(SkillLearningMode mode, int minGoldenCases = SkillLearningDefaults.MinGoldenCasesForAutoApply) =>
        _options.GetAsync(Arg.Any<CancellationToken>()).Returns(LearningModeOptions.Options(mode, minGoldenCases));

    private void GivenPlan(GoldsetItemRef[] holdout, GoldsetItemRef[] train) =>
        _holdoutGate.PlanAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<GoldsetItemRef>>(), Arg.Any<CancellationToken>())
            .Returns(new GoldsetReplayPlan(RunId, Model, TurnEvalScorer.ScorerVersion, holdout, train));

    private static IReadOnlyDictionary<GoldsetItemRef, bool?> Verdicts(params (GoldsetItemRef Item, bool? Hit)[] verdicts) =>
        verdicts.ToDictionary(verdict => verdict.Item, verdict => verdict.Hit);

    private ProposedSkillChange GivenPending(string valueBefore = Before, string valueAfter = After)
    {
        var proposal = ProposalFor(_skill, valueBefore, valueAfter);
        _proposals.GetPendingAsync(ProposedChangeFields.Description, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([proposal]);
        return proposal;
    }

    private static ProposedSkillChange ProposalFor(AgentSkill skill, string valueBefore = Before, string valueAfter = After) =>
        new()
        {
            Id = Guid.NewGuid(),
            SkillId = skill.Id,
            SkillName = skill.Name,
            Field = ProposedChangeFields.Description,
            ValueBefore = valueBefore,
            ValueAfter = valueAfter,
            Status = ProposedChangeStatuses.Pending
        };

    private void GivenPendingPair(ProposedSkillChange first, ProposedSkillChange second) =>
        _proposals.GetPendingAsync(ProposedChangeFields.Description, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([first, second]);

    // The skill row is one mutable object, so its final state alone cannot show that the restore was written:
    // the two writes are recorded with the values they carried.
    private async Task ShouldHaveAppliedAndRestored()
    {
        await _skills.Received(2).UpdateAsync(_skill, Arg.Any<CancellationToken>());
        _writtenSkills.ShouldBe([(After, OriginalVersion + 1), (Before, OriginalVersion)]);
    }

    private static JsonElement Metrics(ProposedSkillChange proposal) =>
        JsonDocument.Parse(proposal.GateMetricsJson.ShouldNotBeNull()).RootElement;

    private Task<SkillDescriptionSharpenerResult> Run() => _sharpener.RunAsync(SkillLearningRunTrigger.Manual);

    // The six-hourly tick may fall into the weekly eval that produces the next reference run, and Gate changes
    // the live catalogue for minutes per proposal - so a scheduled run in Gate only collects.
    [Test]
    public async Task InGateMode_AScheduledRun_OnlyCollects()
    {
        GivenMode(SkillLearningMode.Gate);
        var proposal = GivenPending();

        var (passed, blocked, _, _) = await _sharpener.RunAsync(SkillLearningRunTrigger.Scheduled);

        passed.ShouldBe(0);
        blocked.ShouldBe(0);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Pending);
        await _optimizer.Received(1).GenerateProposalsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _skills.DidNotReceive().UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
        await _holdoutGate.DidNotReceiveWithAnyArgs().PlanAsync(default!, default!, default);
        await _oracle.DidNotReceive().FindFailingGoldenCasesAsync(
            Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>());
    }

    // Putting back what an interrupted run left live is not a measurement, so the scheduled run does it too.
    [Test]
    public async Task InGateMode_AScheduledRun_StillPutsBackWhatAnInterruptedRunLeftLive()
    {
        GivenMode(SkillLearningMode.Gate);
        _skill.Description = After;
        _skill.Version = OriginalVersion + 1;
        var proposal = GivenPending();

        await _sharpener.RunAsync(SkillLearningRunTrigger.Scheduled);

        _skill.Description.ShouldBe(Before);
        _skill.Version.ShouldBe(OriginalVersion);
        _writtenSkills.ShouldBe([(Before, OriginalVersion)]);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Pending);
        await _holdoutGate.DidNotReceiveWithAnyArgs().PlanAsync(default!, default!, default);
    }

    [Test]
    public async Task InGateMode_AManualRun_Measures()
    {
        GivenMode(SkillLearningMode.Gate);
        var proposal = GivenPending();

        await _sharpener.RunAsync(SkillLearningRunTrigger.Manual);

        proposal.Status.ShouldBe(ProposedChangeStatuses.GatePassed);
        await _holdoutGate.Received(2).ReplayAsync(Arg.Any<GoldsetReplayPlan>(), Arg.Any<CancellationToken>());
        await ShouldHaveAppliedAndRestored();
    }

    [Test]
    public async Task InAutoApplyMode_AScheduledRun_StillMeasures()
    {
        var proposal = GivenPending();

        await _sharpener.RunAsync(SkillLearningRunTrigger.Scheduled);

        proposal.Status.ShouldBe(ProposedChangeStatuses.AppliedAuto);
    }

    // A hard kill between applying and restoring leaves the never-judged description live with the raised
    // version and the proposal pending. Nothing else produces that state, so the next run puts it back first.
    [Test]
    public async Task AProposalLeftLiveByAnInterruptedRun_IsPutBackAndGatedAgain()
    {
        GivenMode(SkillLearningMode.Gate);
        _skill.Description = After;
        _skill.Version = OriginalVersion + 1;
        var proposal = GivenPending();
        var liveAtGoldenCaseReplays = new List<string>();
        _oracle.FindFailingGoldenCasesAsync(Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                liveAtGoldenCaseReplays.Add(_skill.Description);
                return [];
            });

        var (passed, _, _, _) = await Run();

        liveAtGoldenCaseReplays.ShouldBe([Before, After]);
        passed.ShouldBe(1);
        proposal.Status.ShouldBe(ProposedChangeStatuses.GatePassed);
        _skill.Description.ShouldBe(Before);
        _skill.Version.ShouldBe(OriginalVersion);
        _writtenSkills.ShouldBe([(Before, OriginalVersion), (After, OriginalVersion + 1), (Before, OriginalVersion)]);
        await _indexVerifier.Received(2).IsIndexedAsync(_skill.Name, Before, Arg.Any<CancellationToken>());
        _logger.Received(1).Log(
            LogLevel.Warning, Arg.Any<EventId>(), Arg.Any<object>(), Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task NewProposalsAreAskedForBeforeTheOpenOnesAreDecided()
    {
        _proposals.GetPendingAsync(ProposedChangeFields.Description, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        await Run();

        await _optimizer.Received(1).GenerateProposalsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // Collect never spends the optimizer's paid LLM call - the learning cases stay collected for later, not
    // turned into proposals, so no installation pays for a call it never asked for.
    [Test]
    public async Task InCollectMode_TheOptimizerIsNeverCalledAndNothingIsMeasured()
    {
        GivenMode(SkillLearningMode.Collect);
        var proposal = GivenPending();

        var (passed, blocked, _, _) = await Run();

        passed.ShouldBe(0);
        blocked.ShouldBe(0);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Pending);
        await _optimizer.DidNotReceive().GenerateProposalsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _proposals.DidNotReceive().GetPendingAsync(
            Arg.Any<string>(), SkillLearningDefaults.MaxProposalsPerRun, Arg.Any<CancellationToken>());
        await _skills.DidNotReceive().UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
        await _holdoutGate.DidNotReceiveWithAnyArgs().PlanAsync(default!, default!, default);
    }

    // Gate is triggered manually, so unlike Collect it does ask the optimizer for new proposals.
    [Test]
    public async Task InGateMode_AManualRun_StillCallsTheOptimizer()
    {
        GivenMode(SkillLearningMode.Gate);
        GivenPending();

        await Run();

        await _optimizer.Received(1).GenerateProposalsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // Whatever the optimizer counted this run is carried through unchanged, whether or not anything was
    // pending to gate afterward - a caller needs to tell "nothing pending" apart from "the optimizer failed".
    [Test]
    public async Task TheOptimizersAttemptAndFailureCounters_AreCarriedThrough()
    {
        _proposals.GetPendingAsync(ProposedChangeFields.Description, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _optimizer.GenerateProposalsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new SkillDescriptionOptimizerResult(Generated: 0, Attempts: 3, Failures: 2));

        var result = await Run();

        result.OptimizerAttempts.ShouldBe(3);
        result.OptimizerFailures.ShouldBe(2);
    }

    // An installation switched from Gate back to Collect after an interrupted gate run must not keep the
    // never-judged description: putting it back only ever moves the catalogue towards its reviewed state.
    [Test]
    public async Task InCollectMode_ADescriptionAnInterruptedGateRunLeftLive_IsPutBack()
    {
        GivenMode(SkillLearningMode.Collect);
        _skill.Description = After;
        _skill.Version = OriginalVersion + 1;
        var proposal = GivenPending();

        await Run();

        _skill.Description.ShouldBe(Before);
        _skill.Version.ShouldBe(OriginalVersion);
        _writtenSkills.ShouldBe([(Before, OriginalVersion)]);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Pending);
        await _holdoutGate.DidNotReceiveWithAnyArgs().PlanAsync(default!, default!, default);
    }

    [Test]
    public async Task InAutoApplyMode_ADescriptionAnInterruptedRunLeftLive_IsPutBackBeforeTheGate()
    {
        _skill.Description = After;
        _skill.Version = OriginalVersion + 1;
        var proposal = GivenPending();

        await Run();

        _writtenSkills[0].ShouldBe((Before, OriginalVersion));
        proposal.Status.ShouldBe(ProposedChangeStatuses.AppliedAuto);
        _skill.Description.ShouldBe(After);
        _skill.Version.ShouldBe(OriginalVersion + 1);
    }

    [Test]
    public async Task WithoutOpenProposals_NothingIsReplayed()
    {
        _proposals.GetPendingAsync(ProposedChangeFields.Description, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        var (passed, blocked, _, _) = await Run();

        passed.ShouldBe(0);
        blocked.ShouldBe(0);
        await _oracle.DidNotReceive().FindFailingGoldenCasesAsync(
            Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InGateMode_APassingProposal_IsRecordedAsGatePassedAndNothingStaysLive()
    {
        GivenMode(SkillLearningMode.Gate);
        var proposal = GivenPending();

        var (passed, blocked, _, _) = await Run();

        passed.ShouldBe(1);
        blocked.ShouldBe(0);
        proposal.Status.ShouldBe(ProposedChangeStatuses.GatePassed);
        proposal.ReviewedBy.ShouldBe(SkillLearningDefaults.AutomaticReviewer);
        _skill.Description.ShouldBe(Before);
        _skill.Version.ShouldBe(OriginalVersion);
        await ShouldHaveAppliedAndRestored();
        await _indexVerifier.Received(1).IsIndexedAsync(_skill.Name, After, Arg.Any<CancellationToken>());
        await _indexVerifier.Received(1).IsIndexedAsync(_skill.Name, Before, Arg.Any<CancellationToken>());
        var metrics = Metrics(proposal);
        metrics.GetProperty("verdict").GetString().ShouldBe(GoldsetGateVerdicts.Passed);
        metrics.GetProperty("referenceEvalRunId").GetGuid().ShouldBe(RunId);
        metrics.GetProperty("trainMissesFixed").EnumerateArray().Select(e => e.GetString()).ShouldContain(TrainRef.ItemId);
        metrics.GetProperty("isCalibration").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public async Task InGateMode_AProposalThatFixesNothing_IsRejectedAndPutBack()
    {
        GivenMode(SkillLearningMode.Gate);
        _holdoutGate.ReplayAsync(Arg.Any<GoldsetReplayPlan>(), Arg.Any<CancellationToken>())
            .Returns(Verdicts((TrainRef, false)));
        var proposal = GivenPending();

        var (passed, blocked, _, _) = await Run();

        passed.ShouldBe(0);
        blocked.ShouldBe(0);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Rejected);
        _skill.Description.ShouldBe(Before);
        await ShouldHaveAppliedAndRestored();
        Metrics(proposal).GetProperty("verdict").GetString().ShouldBe(GoldsetGateVerdicts.NoNetGain);
    }

    [Test]
    public async Task AHoldoutRegression_BlocksTheProposalAndNamesTheItem()
    {
        GivenMode(SkillLearningMode.Gate);
        GivenPlan([HoldoutRef], [TrainRef]);
        _holdoutGate.ReplayAsync(Arg.Any<GoldsetReplayPlan>(), Arg.Any<CancellationToken>())
            .Returns(_ => _skill.Description == Before
                ? Verdicts((HoldoutRef, true), (TrainRef, false))
                : Verdicts((HoldoutRef, false), (TrainRef, true)));
        var proposal = GivenPending();

        var (passed, blocked, _, _) = await Run();

        passed.ShouldBe(0);
        blocked.ShouldBe(1);
        _skill.Description.ShouldBe(Before);
        await ShouldHaveAppliedAndRestored();
        proposal.Status.ShouldBe(ProposedChangeStatuses.BlockedRegression);
        proposal.Justification.ShouldStartWith("Blocked by the targeted holdout replay");
        proposal.Justification.ShouldContain(HoldoutRef.ItemId);
        Metrics(proposal).GetProperty("holdoutRegressions").EnumerateArray().Select(e => e.GetString())
            .ShouldContain(HoldoutRef.ItemId);
    }

    // No reference run yet is transient: the next full eval may make the proposal measurable.
    [Test]
    public async Task AProposalWithoutAReferenceRun_StaysPendingAndIsNeverApplied()
    {
        GivenMode(SkillLearningMode.Gate);
        _holdoutGate.PlanAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<GoldsetItemRef>>(), Arg.Any<CancellationToken>())
            .Returns((GoldsetReplayPlan?)null);
        var proposal = GivenPending();

        var (passed, blocked, _, _) = await Run();

        passed.ShouldBe(0);
        blocked.ShouldBe(0);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Pending);
        await _skills.DidNotReceive().UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
    }

    // Nothing to replay against an existing run is permanent: kept pending, the proposal would occupy a slot of
    // the three-row pending window on every run and block its skill for good.
    [Test]
    public async Task AProposalWithNothingToReplay_IsRejectedWithoutBeingApplied()
    {
        GivenMode(SkillLearningMode.Gate);
        GivenPlan([], []);
        var proposal = GivenPending();

        var (passed, blocked, _, _) = await Run();

        passed.ShouldBe(0);
        blocked.ShouldBe(0);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Rejected);
        proposal.Justification.ShouldStartWith("Rejected without measurement");
        Metrics(proposal).GetProperty("verdict").GetString().ShouldBe(GoldsetGateVerdicts.NotMeasured);
        await _skills.DidNotReceive().UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
        await _holdoutGate.DidNotReceive().ReplayAsync(Arg.Any<GoldsetReplayPlan>(), Arg.Any<CancellationToken>());
    }

    // A provider outage can leave the replay with the current description without a single answer. The pair
    // can then never be measured, so nothing is applied - the proposal is not judged yet, but the attempt is
    // recorded.
    [Test]
    public async Task ABaselineReplayWithoutAnAnsweredItem_IsNotMeasuredAndNothingIsApplied()
    {
        GivenMode(SkillLearningMode.Gate);
        _holdoutGate.ReplayAsync(Arg.Any<GoldsetReplayPlan>(), Arg.Any<CancellationToken>())
            .Returns(Verdicts((TrainRef, null)));
        var proposal = GivenPending();

        var (passed, blocked, _, _) = await Run();

        passed.ShouldBe(0);
        blocked.ShouldBe(0);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Pending);
        proposal.ReviewedAt.ShouldBeNull();
        _skill.Description.ShouldBe(Before);
        var metrics = Metrics(proposal);
        metrics.GetProperty("verdict").GetString().ShouldBe(GoldsetGateVerdicts.NotMeasured);
        metrics.GetProperty("unmeasuredAttempts").GetInt32().ShouldBe(1);
        metrics.GetProperty("trainMissesReplayed").GetInt32().ShouldBe(1);
        metrics.GetProperty("trainMeasured").GetInt32().ShouldBe(0);
        await _proposals.Received(1).UpdateAsync(proposal, Arg.Any<CancellationToken>());
        await _skills.DidNotReceive().UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
        await _holdoutGate.Received(1).ReplayAsync(Arg.Any<GoldsetReplayPlan>(), Arg.Any<CancellationToken>());
    }

    // With planned holdout items the judge needs at least one of them answered on both sides; a baseline that
    // answered none of them cannot be completed by the second replay.
    [Test]
    public async Task ABaselineReplayWithoutAnAnsweredHoldoutItem_IsNotMeasuredAndNothingIsApplied()
    {
        GivenMode(SkillLearningMode.Gate);
        GivenPlan([HoldoutRef], [TrainRef]);
        _holdoutGate.ReplayAsync(Arg.Any<GoldsetReplayPlan>(), Arg.Any<CancellationToken>())
            .Returns(Verdicts((HoldoutRef, null), (TrainRef, false)));
        var proposal = GivenPending();

        await Run();

        proposal.Status.ShouldBe(ProposedChangeStatuses.Pending);
        Metrics(proposal).GetProperty("verdict").GetString().ShouldBe(GoldsetGateVerdicts.NotMeasured);
        await _skills.DidNotReceive().UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
    }

    // Without a measured holdout item there was no regression check, so a train gain alone must not pass.
    [Test]
    public async Task AHoldoutItemUnansweredAfterApplying_KeepsAMeasuredTrainGainFromPassing()
    {
        GivenMode(SkillLearningMode.Gate);
        GivenPlan([HoldoutRef], [TrainRef]);
        _holdoutGate.ReplayAsync(Arg.Any<GoldsetReplayPlan>(), Arg.Any<CancellationToken>())
            .Returns(_ => _skill.Description == Before
                ? Verdicts((HoldoutRef, true), (TrainRef, false))
                : Verdicts((HoldoutRef, null), (TrainRef, true)));
        var proposal = GivenPending();

        var (passed, blocked, _, _) = await Run();

        passed.ShouldBe(0);
        blocked.ShouldBe(0);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Pending);
        _skill.Description.ShouldBe(Before);
        await ShouldHaveAppliedAndRestored();
        var metrics = Metrics(proposal);
        metrics.GetProperty("verdict").GetString().ShouldBe(GoldsetGateVerdicts.NotMeasured);
        metrics.GetProperty("holdoutReplays").GetInt32().ShouldBe(1);
        metrics.GetProperty("holdoutMeasured").GetInt32().ShouldBe(0);
        metrics.GetProperty("trainMeasured").GetInt32().ShouldBe(1);
        metrics.GetProperty("trainMissesFixed").EnumerateArray().Select(e => e.GetString()).ShouldContain(TrainRef.ItemId);
    }

    // Unmeasurable again in a later run is taken as permanent: kept pending, the proposal would hold a slot of
    // the three-row pending window and cost replays on every run.
    [Test]
    public async Task AGateThatCouldNotBeMeasuredTwice_RejectsTheProposal()
    {
        GivenMode(SkillLearningMode.Gate);
        _holdoutGate.ReplayAsync(Arg.Any<GoldsetReplayPlan>(), Arg.Any<CancellationToken>())
            .Returns(Verdicts((TrainRef, null)));
        var proposal = GivenPending();

        await Run();
        var (passed, blocked, _, _) = await Run();

        passed.ShouldBe(0);
        blocked.ShouldBe(0);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Rejected);
        proposal.ReviewedBy.ShouldBe(SkillLearningDefaults.AutomaticReviewer);
        proposal.Justification.ShouldStartWith("Rejected without a verdict");
        _skill.Description.ShouldBe(Before);
        var metrics = Metrics(proposal);
        metrics.GetProperty("verdict").GetString().ShouldBe(GoldsetGateVerdicts.NotMeasured);
        metrics.GetProperty("unmeasuredAttempts").GetInt32().ShouldBe(SkillLearningDefaults.MaxUnmeasuredGateAttempts);
    }

    [Test]
    public async Task APairUnmeasurableAfterApplyingTwice_RejectsTheProposal()
    {
        GivenMode(SkillLearningMode.Gate);
        GivenPlan([HoldoutRef], [TrainRef]);
        _holdoutGate.ReplayAsync(Arg.Any<GoldsetReplayPlan>(), Arg.Any<CancellationToken>())
            .Returns(_ => _skill.Description == Before
                ? Verdicts((HoldoutRef, true), (TrainRef, false))
                : Verdicts((HoldoutRef, null), (TrainRef, true)));
        var proposal = GivenPending();

        await Run();
        proposal.Status.ShouldBe(ProposedChangeStatuses.Pending);
        await Run();

        proposal.Status.ShouldBe(ProposedChangeStatuses.Rejected);
        _skill.Description.ShouldBe(Before);
        Metrics(proposal).GetProperty("unmeasuredAttempts").GetInt32().ShouldBe(SkillLearningDefaults.MaxUnmeasuredGateAttempts);
        await _skills.Received(4).UpdateAsync(_skill, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnEarlierMeasuredVerdict_DoesNotCountAsAnUnmeasuredAttempt()
    {
        GivenMode(SkillLearningMode.Gate);
        _holdoutGate.ReplayAsync(Arg.Any<GoldsetReplayPlan>(), Arg.Any<CancellationToken>())
            .Returns(Verdicts((TrainRef, null)));
        var proposal = GivenPending();
        proposal.GateMetricsJson = "{\"verdict\":\"" + GoldsetGateVerdicts.NoNetGain + "\",\"unmeasuredAttempts\":1}";

        await Run();

        proposal.Status.ShouldBe(ProposedChangeStatuses.Pending);
        Metrics(proposal).GetProperty("unmeasuredAttempts").GetInt32().ShouldBe(1);
    }

    [Test]
    public async Task AGoldsetBornProposal_HandsItsEvalItemsToThePlan()
    {
        var proposal = GivenPending();
        proposal.Origin = ProposedChangeOrigins.GoldsetEval;
        proposal.EvidenceJson = GoldsetMissEvidenceCodec.Serialize(new GoldsetMissEvidence(["Umsatz pro Kunde"], [TrainRef]));

        await Run();

        await _holdoutGate.Received(1).PlanAsync(
            _skill.Name,
            Arg.Is<IReadOnlyList<GoldsetItemRef>>(items => items.Count == 1 && items[0] == TrainRef),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EveryProposal_IsJudgedOnAPairOfReplays()
    {
        GivenPending();

        await Run();

        await _holdoutGate.Received(2).ReplayAsync(Arg.Any<GoldsetReplayPlan>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnIndexThatDoesNotShowTheProposal_LeavesItPendingAndPutsTheDescriptionBack()
    {
        GivenMode(SkillLearningMode.Gate);
        _indexVerifier.IsIndexedAsync(_skill.Name, After, Arg.Any<CancellationToken>()).Returns(false);
        var proposal = GivenPending();

        var (passed, blocked, _, _) = await Run();

        passed.ShouldBe(0);
        blocked.ShouldBe(0);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Pending);
        _skill.Description.ShouldBe(Before);
        await _holdoutGate.Received(1).ReplayAsync(Arg.Any<GoldsetReplayPlan>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ARestoreTheIndexDoesNotConfirm_AbortsTheRunNamingTheSkill()
    {
        GivenMode(SkillLearningMode.Gate);
        _indexVerifier.IsIndexedAsync(_skill.Name, Before, Arg.Any<CancellationToken>()).Returns(false);
        var proposal = GivenPending();

        var exception = await Should.ThrowAsync<SkillIndexNotRestoredException>(() => Run());

        exception.SkillName.ShouldBe(_skill.Name);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Pending);
    }

    [Test]
    public async Task ANullProposal_IsStoredAsCalibrationAndNeverPasses()
    {
        GivenMode(SkillLearningMode.Gate);
        _holdoutGate.ReplayAsync(Arg.Any<GoldsetReplayPlan>(), Arg.Any<CancellationToken>())
            .Returns(Verdicts((TrainRef, false)), Verdicts((TrainRef, true)));
        var proposal = GivenPending(Before, Before);

        var (passed, _, _, _) = await Run();

        passed.ShouldBe(0);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Rejected);
        var metrics = Metrics(proposal);
        metrics.GetProperty("isCalibration").GetBoolean().ShouldBeTrue();
        metrics.GetProperty("netGain").GetInt32().ShouldBe(1);
    }

    [Test]
    public async Task InAutoApplyMode_APassingChangeStaysLiveWithABumpedVersion()
    {
        var proposal = GivenPending();

        var (passed, blocked, _, _) = await Run();

        passed.ShouldBe(1);
        blocked.ShouldBe(0);
        _skill.Description.ShouldBe(After);
        _skill.Version.ShouldBe(OriginalVersion + 1);
        proposal.Status.ShouldBe(ProposedChangeStatuses.AppliedAuto);
        proposal.ReviewedAt.ShouldNotBeNull();
    }

    [Test]
    public async Task AChangeThatBreaksAGoldenCase_IsRolledBackAndBlocked()
    {
        var proposal = GivenPending();
        _oracle.FindFailingGoldenCasesAsync(Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>())
            .Returns([], ["'kunde anlegen' no longer reaches 'create_client'"]);

        var (passed, blocked, _, _) = await Run();

        passed.ShouldBe(0);
        blocked.ShouldBe(1);
        _skill.Description.ShouldBe(Before);
        _skill.Version.ShouldBe(OriginalVersion);
        proposal.Status.ShouldBe(ProposedChangeStatuses.BlockedRegression);
        proposal.Justification.ShouldStartWith("Blocked by the routing regression gate");
        proposal.Justification.ShouldContain("create_client");
        Metrics(proposal).GetProperty("verdict").GetString().ShouldBe(GoldsetGateVerdicts.GoldenCaseRegression);
        await _refresher.Received(2).RefreshAndWaitForIndexAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AGoldenCaseThatWasAlreadyFailing_DoesNotBlockTheChange()
    {
        var proposal = GivenPending();
        _oracle.FindFailingGoldenCasesAsync(Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>())
            .Returns(["already broken"]);

        var (passed, _, _, _) = await Run();

        passed.ShouldBe(1);
        proposal.Status.ShouldBe(ProposedChangeStatuses.AppliedAuto);
    }

    // Left pending forever would lock the skill for every later proposal, exactly the trap a vetoed export
    // fell into; rejecting it with a justification frees the slot instead.
    [Test]
    public async Task AProposalWrittenAgainstAnOlderDescription_IsRejectedWithAJustification()
    {
        var proposal = GivenPending("Something else entirely.");

        var (passed, blocked, _, _) = await Run();

        passed.ShouldBe(0);
        blocked.ShouldBe(0);
        _skill.Description.ShouldBe(Before);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Rejected);
        proposal.Justification.ShouldContain(_skill.Name);
        await _skills.DidNotReceive().UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AGateThatThrows_PutsTheDescriptionBackAndLeavesTheProposalPending()
    {
        var proposal = GivenPending();
        _oracle.FindFailingGoldenCasesAsync(Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>())
            .Returns(_ => [], _ => throw new InvalidOperationException("the index is unreachable"));

        var (passed, blocked, _, _) = await Run();

        passed.ShouldBe(0);
        blocked.ShouldBe(0);
        _skill.Description.ShouldBe(Before);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Pending);
        proposal.ReviewedAt.ShouldBeNull();
        await _refresher.Received(2).RefreshAndWaitForIndexAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnApplyThatThrows_IsPutBackAndLeavesTheProposalPending()
    {
        GivenMode(SkillLearningMode.Gate);
        var proposal = GivenPending();
        var updateCalls = 0;
        _skills.When(x => x.UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                if (++updateCalls == 1)
                {
                    throw new InvalidOperationException("the database hiccuped");
                }
            });

        var (passed, blocked, _, _) = await Run();

        passed.ShouldBe(0);
        blocked.ShouldBe(0);
        _skill.Description.ShouldBe(Before);
        _skill.Version.ShouldBe(OriginalVersion);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Pending);
        await _skills.Received(2).UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ARunCancelledWhileMeasuring_StillPutsTheDescriptionBack()
    {
        GivenMode(SkillLearningMode.Gate);
        var proposal = GivenPending();
        using var cancellation = new CancellationTokenSource();
        _oracle.FindFailingGoldenCasesAsync(Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>())
            .Returns(_ => [], _ =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });

        await Should.ThrowAsync<OperationCanceledException>(() => _sharpener.RunAsync(SkillLearningRunTrigger.Manual, cancellation.Token));

        _skill.Description.ShouldBe(Before);
        _skill.Version.ShouldBe(OriginalVersion);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Pending);
        await _refresher.Received(1).RefreshAndWaitForIndexAsync(
            Arg.Is<string>(reason => reason.StartsWith("restoring")), CancellationToken.None);
        await _indexVerifier.Received(1).IsIndexedAsync(_skill.Name, Before, CancellationToken.None);
    }

    [Test]
    public async Task AGateThatThrowsOnOneProposal_DoesNotAbortTheRest()
    {
        var first = GivenPending();
        var second = ProposalFor(_skill);
        GivenPendingPair(first, second);

        var throws = true;
        _oracle.FindFailingGoldenCasesAsync(Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>())
            .Returns(_ => [], _ =>
            {
                if (throws)
                {
                    throws = false;
                    throw new InvalidOperationException("the index is unreachable");
                }

                return [];
            });

        var (passed, _, _, _) = await Run();

        passed.ShouldBe(1);
        first.Status.ShouldBe(ProposedChangeStatuses.Pending);
        second.Status.ShouldBe(ProposedChangeStatuses.AppliedAuto);
    }

    [Test]
    public async Task AnAppliedChangeReachesRetrievalThroughACatalogueRefresh()
    {
        GivenPending();

        await Run();

        await _refresher.Received(1).RefreshAndWaitForIndexAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // A restore that fails in the database has to stop the run as loudly as one the index does not confirm; the
    // loop lets only SkillIndexNotRestoredException through.
    [Test]
    public async Task ARestoreThatThrows_FailsTheRunLoudly()
    {
        var proposal = GivenPending();
        _oracle.FindFailingGoldenCasesAsync(Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>())
            .Returns([], ["'kunde anlegen' no longer reaches 'create_client'"]);

        var updateCalls = 0;
        _skills.When(x => x.UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                if (++updateCalls == 2)
                {
                    throw new InvalidOperationException("the database went away");
                }
            });

        var exception = await Should.ThrowAsync<SkillIndexNotRestoredException>(() => Run());

        exception.SkillName.ShouldBe(_skill.Name);
        exception.InnerException.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("database went away");
        proposal.Status.ShouldBe(ProposedChangeStatuses.Pending);
        _logger.Received(1).Log(
            LogLevel.Error, Arg.Any<EventId>(), Arg.Any<object>(), Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task WithTooFewHoldoutGoldenCases_TheProposalStaysPendingAndTheDescriptionIsUntouched()
    {
        var proposal = GivenPending();
        _goldenCases.CountHoldoutAsync(Arg.Any<CancellationToken>()).Returns(SkillLearningDefaults.MinGoldenCasesForAutoApply - 1);

        var (passed, blocked, _, _) = await Run();

        passed.ShouldBe(0);
        blocked.ShouldBe(0);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Pending);
        await _skills.DidNotReceive().UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
        await _oracle.DidNotReceive().FindFailingGoldenCasesAsync(
            Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AMinimumRaisedInTheSettings_IsWhatTheGateCompares()
    {
        var proposal = GivenPending();
        GivenMode(SkillLearningMode.AutoApply, RaisedMinimum);

        var (passed, blocked, _, _) = await Run();

        passed.ShouldBe(0);
        blocked.ShouldBe(0);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Pending);
        await _oracle.DidNotReceive().FindFailingGoldenCasesAsync(
            Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheGateReplaysTheHoldoutGoldenCasesOfTheSkillOnly()
    {
        GivenPending();

        await Run();

        await _goldenCases.Received().ListHoldoutAsync(
            SkillLearningDefaults.MaxGoldenCasesPerRegressionCheck, _skill.Name, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EachProposal_IsGatedOnTheHoldoutCasesOfItsOwnSkill()
    {
        var first = GivenPending();
        var other = new AgentSkill { Id = Guid.NewGuid(), Name = OtherSkill, Description = Before, Version = 1 };
        _skills.GetByIdAsync(other.Id, Arg.Any<CancellationToken>()).Returns(other);
        GivenPendingPair(first, ProposalFor(other));

        await Run();

        await _goldenCases.Received(1).ListHoldoutAsync(
            SkillLearningDefaults.MaxGoldenCasesPerRegressionCheck, _skill.Name, Arg.Any<CancellationToken>());
        await _goldenCases.Received(1).ListHoldoutAsync(
            SkillLearningDefaults.MaxGoldenCasesPerRegressionCheck, other.Name, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TwoProposalsForTheSameSkill_ShareOneBaselinePass()
    {
        var first = GivenPending();
        GivenPendingPair(first, ProposalFor(_skill, After, Narrowed));

        await Run();

        await _goldenCases.Received(1).ListHoldoutAsync(Arg.Any<int>(), _skill.Name, Arg.Any<CancellationToken>());
        await _oracle.Received(3).FindFailingGoldenCasesAsync(
            Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>());
    }
}
