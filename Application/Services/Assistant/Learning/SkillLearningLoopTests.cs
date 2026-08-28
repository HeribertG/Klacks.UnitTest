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
    private ISkillLearningCandidateRepository _candidates = null!;
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

        _candidates = Substitute.For<ISkillLearningCandidateRepository>();
        _generator = Substitute.For<ILearnedArtifactGenerator>();
        _oracle = Substitute.For<ISkillRoutingOracle>();
        _phraseLearner = Substitute.For<IPhraseLearner>();

        _sharpener = Substitute.For<ISkillDescriptionSharpener>();
        _sharpener.RunAsync(Arg.Any<CancellationToken>()).Returns((0, 0));

        _loop = new SkillLearningLoop(
            _clusters, _cases, _candidates, _generator, _oracle, _phraseLearner, _sharpener,
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

    private void GivenProbe(bool found, params string[] offered) =>
        _oracle.ProbeAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SkillRoutingProbe(found, offered));

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

    // Stage G2 cannot compose skills, so a composable wish is recorded for stage G3 and closed as
    // unservable for now rather than re-classified by a model every six hours forever.
    [Test]
    public async Task AComposableWish_IsRecordedForCapabilityLearning()
    {
        var cluster = GivenReadyCluster();
        GivenProbe(false, "list_clients");
        GivenClassification(cluster.Id, SkillLearningClassifications.Composable, null, "chain two skills");

        var summary = await _loop.RunAsync();

        summary.Unfulfillable.ShouldBe(1);
        await _candidates.Received(1).AddAsync(
            Arg.Is<SkillLearningCandidate>(candidate =>
                candidate.ClusterId == cluster.Id
                && candidate.Kind == SkillLearningCandidateKinds.Capability),
            Arg.Any<CancellationToken>());
    }

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
