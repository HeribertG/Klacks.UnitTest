// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the orchestration of a learning run. The behaviours worth pinning are the ones that decide
/// whether the loop is safe to leave running unattended: a cluster another run already claimed is skipped,
/// a wish whose target is already retrievable is closed without learning anything, a failed round costs
/// exactly one attempt and the attempt budget - not an outage - is what turns a wish unfulfillable, and a
/// failure on one cluster never takes the run down with it.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Learning;

using Klacks.Api.Application.Services.Assistant.Learning;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SkillLearningLoopTests
{
    private const string Target = "revenue_per_client";
    private const string Excerpt = "Zeige mir die Umsatzstatistik pro Kunde";

    private ISkillLearningClusterRepository _clusters = null!;
    private ISkillLearningCaseRepository _cases = null!;
    private ICapabilityLearner _capabilityLearner = null!;
    private ILearnedArtifactGenerator _generator = null!;
    private ISkillRoutingOracle _oracle = null!;
    private IPhraseLearner _phraseLearner = null!;
    private ISkillDescriptionSharpener _sharpener = null!;
    private SkillLearningLoop _loop = null!;

    [SetUp]
    public void SetUp()
    {
        _clusters = Substitute.For<ISkillLearningClusterRepository>();
        _clusters.TryClaimForLearningAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _cases = Substitute.For<ISkillLearningCaseRepository>();
        _cases.ListByClusterAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        _capabilityLearner = Substitute.For<ICapabilityLearner>();
        _generator = Substitute.For<ILearnedArtifactGenerator>();
        _oracle = Substitute.For<ISkillRoutingOracle>();
        _phraseLearner = Substitute.For<IPhraseLearner>();

        _sharpener = Substitute.For<ISkillDescriptionSharpener>();
        _sharpener.RunAsync(Arg.Any<CancellationToken>()).Returns((0, 0));

        _loop = new SkillLearningLoop(
            _clusters, _cases, _generator, _oracle, _phraseLearner, _capabilityLearner, _sharpener,
            Substitute.For<ILogger<SkillLearningLoop>>());
    }

    private SkillLearningCluster GivenReadyCluster(int attemptCount = 0)
    {
        var cluster = new SkillLearningCluster
        {
            Id = Guid.NewGuid(),
            IntentExcerpt = Excerpt,
            Locale = "de",
            Status = SkillLearningClusterStatuses.Ready,
            AttemptCount = attemptCount
        };

        _clusters.ListByStatusAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([cluster]);

        return cluster;
    }

    private void GivenCorrection(Guid clusterId, string expectedSkill) =>
        _cases.ListByClusterAsync(clusterId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new SkillLearningCase { ClusterId = clusterId, ExpectedSkill = expectedSkill }]);

    // The reachable list defaults to the offered one, so every case written before the two were told apart
    // keeps its original meaning. GivenReachable widens it where a test cares.
    private void GivenProbe(bool found, params string[] offered)
    {
        _oracle.ProbeAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SkillRoutingProbe(found, offered));
        GivenReachable(offered);
    }

    private void GivenReachable(params string[] names) =>
        _oracle.ListReachableSkillsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>>(names);

    private void GivenClassification(Guid clusterId, string kind, string? skill = null, string? reason = null) =>
        _generator.ClassifyAsync(Arg.Any<IReadOnlyList<SkillLearningTriageInput>>(), Arg.Any<CancellationToken>())
            .Returns([new SkillLearningClassification(clusterId, kind, skill, reason)]);

    private void GivenPhraseLearned(Guid phraseId) =>
        _phraseLearner.LearnAsync(
                Arg.Any<SkillLearningClusterContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PhraseLearningOutcome.Success(phraseId, "umsatz pro kunde"));

    private void GivenPhraseFailed(string error) =>
        _phraseLearner.LearnAsync(
                Arg.Any<SkillLearningClusterContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PhraseLearningOutcome.Failure(error));

    // The classifier answers for the whole batch at once, so its failure is the batch's failure - and a
    // claim nobody is working on would otherwise sit in learning until the stale sweep expires it an hour
    // later. The attempt count must survive untouched: an outage is not a failed attempt.
    [Test]
    public async Task AClassifierThatThrows_HandsEveryClaimedClusterStraightBackToReady()
    {
        var cluster = GivenReadyCluster(attemptCount: 1);
        GivenProbe(false);
        _generator.ClassifyAsync(
                Arg.Any<IReadOnlyList<SkillLearningTriageInput>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<SkillLearningClassification>>(_ =>
                throw new InvalidOperationException("the model is unreachable"));

        var summary = await _loop.RunAsync();

        summary.Failed.ShouldBe(1);
        await _clusters.Received(1).FinishLearningAsync(
            cluster.Id,
            Arg.Is(SkillLearningClusterStatuses.Ready),
            Arg.Is((string?)null),
            Arg.Is((string?)null),
            Arg.Is<string?>(reason => reason != null && reason.Contains("unreachable")),
            1,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AClassifierThatThrows_LearnsNothing()
    {
        GivenReadyCluster();
        GivenProbe(false);
        _generator.ClassifyAsync(
                Arg.Any<IReadOnlyList<SkillLearningTriageInput>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<SkillLearningClassification>>(_ =>
                throw new InvalidOperationException("the model is unreachable"));

        await _loop.RunAsync();

        await _phraseLearner.DidNotReceive().LearnAsync(
            Arg.Any<SkillLearningClusterContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // The sharpening is the second, independent half of a run. A cluster that already finished must keep
    // its outcome when the sharpening falls over, or the next run would redo work that succeeded.
    [Test]
    public async Task ASharpeningThatThrows_DoesNotTakeTheRunDownWithIt()
    {
        var cluster = GivenReadyCluster();
        GivenCorrection(cluster.Id, Target);
        GivenProbe(false);
        GivenClassification(cluster.Id, SkillLearningClassifications.PhraseGap, Target);
        GivenPhraseLearned(Guid.NewGuid());
        _sharpener.RunAsync(Arg.Any<CancellationToken>())
            .Returns<(int, int)>(_ => throw new InvalidOperationException("the optimizer is unreachable"));

        var summary = await _loop.RunAsync();

        summary.Learned.ShouldBe(1);
        summary.Sharpened.ShouldBe(0);
        summary.Blocked.ShouldBe(0);
    }

    [Test]
    public async Task EveryRunFirstReleasesClaimsAProcessDiedOn()
    {
        _clusters.ListByStatusAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await _loop.RunAsync();

        await _clusters.Received(1).ReleaseStaleClaimsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    // Two overlapping runs must not learn the same wish twice; the claim is the compare-and-swap that
    // decides, and the loser has to walk away rather than proceed on a stale read.
    [Test]
    public async Task AClusterAnotherRunClaimed_IsNotProcessed()
    {
        GivenReadyCluster();
        _clusters.TryClaimForLearningAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var summary = await _loop.RunAsync();

        summary.Processed.ShouldBe(0);
        await _generator.DidNotReceive().ClassifyAsync(
            Arg.Any<IReadOnlyList<SkillLearningTriageInput>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WithNoReadyCluster_NoModelIsAskedAnything()
    {
        _clusters.ListByStatusAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await _loop.RunAsync();

        await _generator.DidNotReceive().ClassifyAsync(
            Arg.Any<IReadOnlyList<SkillLearningTriageInput>>(), Arg.Any<CancellationToken>());
    }

    // An idle run must stay observable: without a summary line, a stuck loop and a healthy but quiet one
    // look identical from the logs, and nobody notices the loop stopped working.
    [Test]
    public async Task AnIdleRun_StillLogsItsSummary()
    {
        var logger = Substitute.For<ILogger<SkillLearningLoop>>();
        var loop = new SkillLearningLoop(
            _clusters, _cases, _generator, _oracle, _phraseLearner, _capabilityLearner, _sharpener,
            logger);
        _clusters.ListByStatusAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await loop.RunAsync();

        logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // If the corrected target is already retrievable there was never a routing gap - learning a phrase for
    // it would add noise and claim credit for what retrieval already did.
    [Test]
    public async Task AWishWhoseCorrectedTargetIsAlreadyOffered_IsDismissedWithoutAModel()
    {
        var cluster = GivenReadyCluster();
        GivenCorrection(cluster.Id, Target);
        GivenProbe(true, Target);

        var summary = await _loop.RunAsync();

        summary.AlreadyRouted.ShouldBe(1);
        await _clusters.Received(1).FinishLearningAsync(
            cluster.Id,
            Arg.Is(SkillLearningClusterStatuses.Dismissed),
            Arg.Is((string?)null),
            Arg.Is((string?)null),
            Arg.Is<string?>(reason => reason != null && reason.Contains("Already routed")),
            0,
            Arg.Any<CancellationToken>());
        await _generator.DidNotReceive().ClassifyAsync(
            Arg.Any<IReadOnlyList<SkillLearningTriageInput>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AWishTheClassifierPointsAtAnAlreadyOfferedSkill_IsDismissedWithoutLearning()
    {
        var cluster = GivenReadyCluster();
        GivenProbe(false, "list_clients");
        GivenClassification(cluster.Id, SkillLearningClassifications.PhraseGap, "list_clients");

        var summary = await _loop.RunAsync();

        summary.AlreadyRouted.ShouldBe(1);
        await _phraseLearner.DidNotReceive().LearnAsync(
            Arg.Any<SkillLearningClusterContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ALearnedPhrase_ClosesTheClusterWithTheArtefactItProduced()
    {
        var cluster = GivenReadyCluster();
        var phraseId = Guid.NewGuid();
        GivenProbe(false, "list_clients");
        GivenClassification(cluster.Id, SkillLearningClassifications.PhraseGap, Target);
        GivenPhraseLearned(phraseId);

        var summary = await _loop.RunAsync();

        summary.Learned.ShouldBe(1);
        await _clusters.Received(1).FinishLearningAsync(
            cluster.Id, SkillLearningClusterStatuses.LearnedPhrase, SkillLearningOutcomeKinds.Phrase,
            phraseId.ToString(), null, 0, Arg.Any<CancellationToken>());
    }

    // Nobody's guess beats the user's own statement of which skill they meant.
    [Test]
    public async Task TheCorrectedSkillOutranksTheClassifiersChoice()
    {
        var cluster = GivenReadyCluster();
        GivenCorrection(cluster.Id, Target);
        GivenProbe(false, "list_clients");
        GivenClassification(cluster.Id, SkillLearningClassifications.PhraseGap, "list_clients_by_city");
        GivenPhraseLearned(Guid.NewGuid());

        await _loop.RunAsync();

        await _phraseLearner.Received(1).LearnAsync(
            Arg.Any<SkillLearningClusterContext>(), Target, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AFailedRound_SendsTheClusterBackWithTheReasonAndOneMoreAttempt()
    {
        var cluster = GivenReadyCluster();
        GivenProbe(false, "list_clients");
        GivenClassification(cluster.Id, SkillLearningClassifications.PhraseGap, Target);
        GivenPhraseFailed("list_clients was offered instead");

        var summary = await _loop.RunAsync();

        summary.Failed.ShouldBe(1);
        await _clusters.Received(1).FinishLearningAsync(
            cluster.Id, SkillLearningClusterStatuses.Ready, null, null,
            "list_clients was offered instead", 1, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnceTheAttemptBudgetIsSpent_TheWishBecomesUnfulfillable()
    {
        var cluster = GivenReadyCluster(SkillLearningDefaults.MaxLearningAttempts - 1);
        GivenProbe(false, "list_clients");
        GivenClassification(cluster.Id, SkillLearningClassifications.PhraseGap, Target);
        GivenPhraseFailed("still not found");

        var summary = await _loop.RunAsync();

        summary.Unfulfillable.ShouldBe(1);
        await _clusters.Received(1).FinishLearningAsync(
            cluster.Id, SkillLearningClusterStatuses.Unfulfillable, null, null, "still not found",
            SkillLearningDefaults.MaxLearningAttempts, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AWishNoSkillCanServe_BecomesUnfulfillableWithTheReasonGiven()
    {
        var cluster = GivenReadyCluster();
        GivenProbe(false, "list_clients");
        GivenClassification(cluster.Id, SkillLearningClassifications.NeedsCode, null, "no skill reports revenue");

        var summary = await _loop.RunAsync();

        summary.Unfulfillable.ShouldBe(1);
        await _clusters.Received(1).FinishLearningAsync(
            cluster.Id, SkillLearningClusterStatuses.Unfulfillable, null, null,
            "no skill reports revenue", 0, Arg.Any<CancellationToken>());
    }

    // A composable wish is handed to the capability learner with the very candidate list the retrieval
    // produced for it: the pool a composition may be drawn from is what retrieval already offered, not
    // the whole catalogue.
    [Test]
    public async Task AComposableWish_IsHandedToTheCapabilityLearner()
    {
        var cluster = GivenReadyCluster();
        GivenProbe(false, "list_clients");
        GivenClassification(cluster.Id, SkillLearningClassifications.Composable, null, "chain two skills");
        GivenCapabilityOutcome(CapabilityLearningOutcome.Success("learned-revenue-report", false));

        var summary = await _loop.RunAsync();

        summary.Learned.ShouldBe(1);
        await _capabilityLearner.Received(1).LearnAsync(
            Arg.Is<SkillLearningClusterContext>(context => context.ClusterId == cluster.Id),
            Arg.Is<IReadOnlyList<string>>(skills => skills.Contains("list_clients")),
            Arg.Any<CancellationToken>());

        await _clusters.Received(1).FinishLearningAsync(
            cluster.Id,
            SkillLearningClusterStatuses.LearnedCapability,
            SkillLearningOutcomeKinds.Capability,
            "learned-revenue-report",
            null,
            0,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ACompositionNoOracleAccepted_CostsAnAttemptLikeAnyOtherFailedRound()
    {
        var cluster = GivenReadyCluster();
        GivenProbe(false, "list_clients");
        GivenClassification(cluster.Id, SkillLearningClassifications.Composable, null, "chain two skills");
        GivenCapabilityOutcome(CapabilityLearningOutcome.Failure("no composition survived"));

        await _loop.RunAsync();

        await _clusters.Received(1).FinishLearningAsync(
            cluster.Id, SkillLearningClusterStatuses.Ready, null, null, "no composition survived", 1,
            Arg.Any<CancellationToken>());
    }

    // An identity the probe could not mint says nothing about the wish. Spending an attempt on it would
    // let an outage declare a serviceable composition unservable.
    [Test]
    public async Task ACompositionThatCouldNotBeJudged_GoesBackWithoutSpendingAnAttempt()
    {
        var cluster = GivenReadyCluster();
        GivenProbe(false, "list_clients");
        GivenClassification(cluster.Id, SkillLearningClassifications.Composable, null, "chain two skills");
        GivenCapabilityOutcome(CapabilityLearningOutcome.Unjudged("the owner's token was refused"));

        await _loop.RunAsync();

        await _clusters.Received(1).FinishLearningAsync(
            cluster.Id, SkillLearningClusterStatuses.Ready, null, null, "the owner's token was refused", 0,
            Arg.Any<CancellationToken>());
    }

    private void GivenCapabilityOutcome(CapabilityLearningOutcome outcome) =>
        _capabilityLearner
            .LearnAsync(
                Arg.Any<SkillLearningClusterContext>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(outcome);

    // An unreachable model must not spend the budget: the budget exists to stop the loop grinding on a
    // wish nobody can serve, not to declare wishes unservable during an outage.
    [Test]
    public async Task WithoutAVerdict_TheClusterGoesBackWithoutSpendingAnAttempt()
    {
        var cluster = GivenReadyCluster();
        GivenProbe(false, "list_clients");
        _generator.ClassifyAsync(
                Arg.Any<IReadOnlyList<SkillLearningTriageInput>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var summary = await _loop.RunAsync();

        summary.Failed.ShouldBe(1);
        await _clusters.Received(1).FinishLearningAsync(
            cluster.Id,
            Arg.Is(SkillLearningClusterStatuses.Ready),
            Arg.Is((string?)null),
            Arg.Is((string?)null),
            Arg.Any<string?>(),
            0,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AClusterThatThrows_ReleasesItsClaimAndDoesNotFailTheRun()
    {
        var cluster = GivenReadyCluster();
        GivenProbe(false, "list_clients");
        GivenClassification(cluster.Id, SkillLearningClassifications.PhraseGap, Target);
        _phraseLearner.LearnAsync(
                Arg.Any<SkillLearningClusterContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<PhraseLearningOutcome>>(_ => throw new InvalidOperationException("index unavailable"));

        var summary = await _loop.RunAsync();

        summary.Failed.ShouldBe(1);
        await _clusters.Received(1).FinishLearningAsync(
            cluster.Id, SkillLearningClusterStatuses.Ready, null, null, "index unavailable", 0,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheDescriptionSharpeningRunsEvenWhenNoClusterWasReady()
    {
        _clusters.ListByStatusAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _sharpener.RunAsync(Arg.Any<CancellationToken>()).Returns((2, 1));

        var summary = await _loop.RunAsync();

        summary.Sharpened.ShouldBe(2);
        summary.Blocked.ShouldBe(1);
    }

    [Test]
    public async Task AtMostTheConfiguredNumberOfClustersIsClaimedPerRun()
    {
        _clusters.ListByStatusAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await _loop.RunAsync();

        await _clusters.Received(1).ListByStatusAsync(
            Arg.Is<IReadOnlyList<string>>(statuses => statuses.Contains(SkillLearningClusterStatuses.Ready)),
            SkillLearningDefaults.MaxClustersPerRun,
            Arg.Any<CancellationToken>());
    }
}
