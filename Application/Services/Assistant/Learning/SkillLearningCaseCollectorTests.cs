// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the collector that replaced SkillGapDetector. Carries over the detector's five original
/// cases (function call, affirmation, no refusal phrase, first occurrence, repeat occurrence) and adds
/// what is new: the minimum length floor, the correction signals, the implicit correction from the
/// following turn, the threshold promotion and the rule that a terminal cluster is never resurrected.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Learning;

using Klacks.Api.Application.Services.Assistant.Learning;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SkillLearningCaseCollectorTests
{
    private const string RefusalAnswer = "Das ist nicht möglich, dafür habe ich keine Fähigkeit.";
    private const string NeutralAnswer = "Hier sind die drei Mitarbeiter, die du gesucht hast.";
    private const string Wish = "Zeige mir die Umsatzstatistik pro Kunde";

    private static readonly Guid AgentId = Guid.NewGuid();

    private ISkillLearningClusterRepository _clusters = null!;
    private ISkillLearningCaseRepository _cases = null!;
    private ISkillLearningOptionsProvider _options = null!;
    private SkillLearningCaseCollector _collector = null!;

    [SetUp]
    public void SetUp()
    {
        _clusters = Substitute.For<ISkillLearningClusterRepository>();
        _cases = Substitute.For<ISkillLearningCaseRepository>();
        _options = Substitute.For<ISkillLearningOptionsProvider>();
        _options.GetAsync(Arg.Any<CancellationToken>()).Returns(new SkillLearningOptions(3, 2, 30, 90));
        _clusters.TryInsertAsync(Arg.Any<SkillLearningCluster>(), Arg.Any<CancellationToken>()).Returns(true);
        _cases.CountDistinctUsersAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(1);
        _cases.CountBySignalAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, int> { [SkillLearningSignals.Refusal] = 1 });

        _collector = new SkillLearningCaseCollector(
            _clusters, _cases, _options, Substitute.For<ILogger<SkillLearningCaseCollector>>());
    }

    private static SkillLearningTurn Turn(
        string message = Wish,
        string response = RefusalAnswer,
        bool hadFunctionCalls = false,
        string? userId = "user-1") =>
        new(AgentId, message, response, hadFunctionCalls, userId, "conv-1", "de", null, ["get_time"]);

    [Test]
    public async Task ATurnWithAFunctionCall_IsNotACase()
    {
        await _collector.CollectFromTurnAsync(Turn(hadFunctionCalls: true));

        await _cases.DidNotReceive().AddAsync(Arg.Any<SkillLearningCase>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnAffirmation_IsNotACase()
    {
        await _collector.CollectFromTurnAsync(Turn(message: "ja mach das"));

        await _cases.DidNotReceive().AddAsync(Arg.Any<SkillLearningCase>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnAnswerWithoutARefusalPhrase_IsNotACase()
    {
        await _collector.CollectFromTurnAsync(Turn(response: NeutralAnswer));

        await _cases.DidNotReceive().AddAsync(Arg.Any<SkillLearningCase>(), Arg.Any<CancellationToken>());
    }

    // Below three words a refusal phrase matches noise ("was?", "und jetzt") far more often than a real
    // capability wish - the floor is what the old detector lacked.
    [Test]
    public async Task AMessageShorterThanThreeWords_IsNotACase()
    {
        await _collector.CollectFromTurnAsync(Turn(message: "Umsatzstatistik bitte"));

        await _cases.DidNotReceive().AddAsync(Arg.Any<SkillLearningCase>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AFirstRefusal_CreatesTheClusterAndTheCase()
    {
        SkillLearningCluster? inserted = null;
        _clusters.TryInsertAsync(Arg.Do<SkillLearningCluster>(c => inserted = c), Arg.Any<CancellationToken>())
            .Returns(true);

        await _collector.CollectFromTurnAsync(Turn());

        inserted.ShouldNotBeNull();
        inserted!.ClusterKey.ShouldBe(MessageNormalizer.Hash(Wish));
        inserted.Status.ShouldBe(SkillLearningClusterStatuses.Collecting);
        inserted.IntentExcerpt.ShouldBe(Wish);
        await _cases.Received(1).AddAsync(
            Arg.Is<SkillLearningCase>(c => c.Signal == SkillLearningSignals.Refusal), Arg.Any<CancellationToken>());
        await _clusters.Received(1).RegisterOccurrenceAsync(
            inserted.Id, Arg.Any<DateTime>(), 1, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // The whole point of the cluster: the raw message must never reach the database.
    [Test]
    public async Task TheStoredExcerpt_NeverExceedsTheLimit()
    {
        var longWish = string.Join(' ', Enumerable.Repeat("Umsatzstatistik", 40));
        SkillLearningCase? recorded = null;
        await _cases.AddAsync(Arg.Do<SkillLearningCase>(c => recorded = c), Arg.Any<CancellationToken>());

        await _collector.CollectFromTurnAsync(Turn(message: longWish));

        recorded.ShouldNotBeNull();
        recorded!.IntentExcerpt.Length.ShouldBeLessThanOrEqualTo(SkillLearningDefaults.ExcerptMaxLength);
    }

    [Test]
    public async Task ARepeatedWish_ReusesTheExistingCluster()
    {
        var existing = ExistingCluster(occurrenceCount: 1);
        _clusters.FindByKeyAsync(AgentId, MessageNormalizer.Hash(Wish), Arg.Any<CancellationToken>())
            .Returns(existing);

        await _collector.CollectFromTurnAsync(Turn());

        await _clusters.DidNotReceive().TryInsertAsync(Arg.Any<SkillLearningCluster>(), Arg.Any<CancellationToken>());
        await _clusters.Received(1).RegisterOccurrenceAsync(
            existing.Id, Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReachingTheOccurrenceThreshold_PromotesTheClusterToReady()
    {
        var existing = ExistingCluster(occurrenceCount: 2);
        _clusters.FindByKeyAsync(AgentId, MessageNormalizer.Hash(Wish), Arg.Any<CancellationToken>())
            .Returns(existing);

        await _collector.CollectFromTurnAsync(Turn());

        await _clusters.Received(1).TryTransitionAsync(
            existing.Id,
            SkillLearningClusterStatuses.Collecting,
            SkillLearningClusterStatuses.Ready,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReachingTheDistinctUserThreshold_PromotesTheClusterEvenWithoutRepetitions()
    {
        var existing = ExistingCluster(occurrenceCount: 0);
        _clusters.FindByKeyAsync(AgentId, MessageNormalizer.Hash(Wish), Arg.Any<CancellationToken>())
            .Returns(existing);
        _cases.CountDistinctUsersAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(2);

        await _collector.CollectFromTurnAsync(Turn());

        await _clusters.Received(1).TryTransitionAsync(
            existing.Id,
            SkillLearningClusterStatuses.Collecting,
            SkillLearningClusterStatuses.Ready,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BelowBothThresholds_TheClusterKeepsCollecting()
    {
        var existing = ExistingCluster(occurrenceCount: 1);
        _clusters.FindByKeyAsync(AgentId, MessageNormalizer.Hash(Wish), Arg.Any<CancellationToken>())
            .Returns(existing);

        await _collector.CollectFromTurnAsync(Turn());

        await _clusters.DidNotReceive().TryTransitionAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // A wish an administrator discarded must not come back on the next occurrence of the same sentence.
    [Test]
    public async Task ADismissedCluster_IsNotCountedAgain()
    {
        var dismissed = ExistingCluster(occurrenceCount: 5);
        dismissed.Status = SkillLearningClusterStatuses.Dismissed;
        _clusters.FindByKeyAsync(AgentId, MessageNormalizer.Hash(Wish), Arg.Any<CancellationToken>())
            .Returns(dismissed);

        await _collector.CollectFromTurnAsync(Turn());

        await _cases.DidNotReceive().AddAsync(Arg.Any<SkillLearningCase>(), Arg.Any<CancellationToken>());
    }

    // Losing the insert race is normal, not an error: the winner's row is re-read and counted.
    [Test]
    public async Task LosingTheInsertRace_CountsAgainstTheWinnersCluster()
    {
        var winner = ExistingCluster(occurrenceCount: 0);
        _clusters.TryInsertAsync(Arg.Any<SkillLearningCluster>(), Arg.Any<CancellationToken>()).Returns(false);
        _clusters.FindByKeyAsync(AgentId, MessageNormalizer.Hash(Wish), Arg.Any<CancellationToken>())
            .Returns((SkillLearningCluster?)null, winner);

        await _collector.CollectFromTurnAsync(Turn());

        await _clusters.Received(1).RegisterOccurrenceAsync(
            winner.Id, Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ACorrection_RecordsTheExpectedSkill()
    {
        SkillLearningCase? recorded = null;
        await _cases.AddAsync(Arg.Do<SkillLearningCase>(c => recorded = c), Arg.Any<CancellationToken>());

        await _collector.CollectCorrectionAsync(new SkillLearningCorrection(
            AgentId, Wish, SkillLearningSignals.WrongSkill, "user-1", "de", "list_clients", "list_orders", null));

        recorded.ShouldNotBeNull();
        recorded!.Signal.ShouldBe(SkillLearningSignals.WrongSkill);
        recorded.ChosenSkill.ShouldBe("list_clients");
        recorded.ExpectedSkill.ShouldBe("list_orders");
    }

    [Test]
    public async Task ACorrectionWithAnUnknownSignal_IsIgnored()
    {
        await _collector.CollectCorrectionAsync(new SkillLearningCorrection(
            AgentId, Wish, CorrectionTypes.WrongParam, "user-1", "de", null, null, null));

        await _cases.DidNotReceive().AddAsync(Arg.Any<SkillLearningCase>(), Arg.Any<CancellationToken>());
    }

    // A refusal is something an assistant says, never something a user corrects. Validating the correction
    // path against every learning signal would let it through and open a cluster on a signal no correction
    // can carry.
    [Test]
    public async Task ARefusalArrivingThroughTheCorrectionPath_IsIgnored()
    {
        await _collector.CollectCorrectionAsync(new SkillLearningCorrection(
            AgentId, Wish, SkillLearningSignals.Refusal, "user-1", "de", null, null, null));

        await _cases.DidNotReceive().AddAsync(Arg.Any<SkillLearningCase>(), Arg.Any<CancellationToken>());
    }

    // One unhappy exchange is one case. The refusal and the negation in the following turn are the same
    // moment seen twice, and counting both would move a cluster a third of the way to the threshold on a
    // single failed answer.
    [Test]
    public async Task ASecondSignalFromTheSameUserInsideTheWindow_IsNotCountedAgain()
    {
        var existing = ExistingCluster(occurrenceCount: 1);
        _clusters.FindByKeyAsync(AgentId, MessageNormalizer.Hash(Wish), Arg.Any<CancellationToken>())
            .Returns(existing);
        _cases.HasCaseSinceAsync(existing.Id, "user-1", Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await _collector.CollectFromTurnAsync(Turn());

        await _cases.DidNotReceive().AddAsync(Arg.Any<SkillLearningCase>(), Arg.Any<CancellationToken>());
        await _clusters.DidNotReceive().RegisterOccurrenceAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheDeduplicationWindowIsScopedToTheUserWhoAsked()
    {
        var existing = ExistingCluster(occurrenceCount: 1);
        _clusters.FindByKeyAsync(AgentId, MessageNormalizer.Hash(Wish), Arg.Any<CancellationToken>())
            .Returns(existing);
        _cases.HasCaseSinceAsync(existing.Id, "user-1", Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await _collector.CollectFromTurnAsync(Turn(userId: "user-2"));

        await _cases.Received(1).AddAsync(Arg.Any<SkillLearningCase>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ARepositoryFailure_IsSwallowed()
    {
        _clusters.FindByKeyAsync(AgentId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<SkillLearningCluster?>>(_ => throw new InvalidOperationException("database down"));

        await Should.NotThrowAsync(() => _collector.CollectFromTurnAsync(Turn()));
    }

    [Test]
    public async Task AnImplicitCorrection_CountsAgainstTheClusterOfThePrecedingUtterance()
    {
        SkillLearningCluster? inserted = null;
        SkillLearningCase? recorded = null;
        _clusters.TryInsertAsync(Arg.Do<SkillLearningCluster>(c => inserted = c), Arg.Any<CancellationToken>())
            .Returns(true);
        await _cases.AddAsync(Arg.Do<SkillLearningCase>(c => recorded = c), Arg.Any<CancellationToken>());
        var trajectoryId = Guid.NewGuid();

        await _collector.CollectImplicitCorrectionAsync(new SkillLearningImplicitCorrection(
            AgentId,
            MessageNormalizer.Hash(Wish),
            Wish,
            "user-1",
            "de",
            "list_clients",
            "[{\"name\":\"list_clients\"}]",
            trajectoryId));

        inserted.ShouldNotBeNull();
        inserted!.ClusterKey.ShouldBe(MessageNormalizer.Hash(Wish));
        recorded.ShouldNotBeNull();
        recorded!.Signal.ShouldBe(SkillLearningSignals.Implicit);
        recorded.ChosenSkill.ShouldBe("list_clients");
        recorded.ExpectedSkill.ShouldBeNull();
        recorded.TrajectoryId.ShouldBe(trajectoryId);
        recorded.ToolsetJson.ShouldBe("[{\"name\":\"list_clients\"}]");
    }

    // The preceding message is gone by the time the negation arrives, only its stored hash points at the
    // right cluster. Hashing the excerpt instead would open a rival cluster for every long utterance.
    [Test]
    public async Task AnImplicitCorrection_KeysOnTheStoredHashNotOnTheExcerpt()
    {
        var longWish = string.Join(' ', Enumerable.Repeat("Umsatzstatistik", 40));
        var excerpt = MessageNormalizer.Excerpt(longWish, SkillLearningDefaults.ExcerptMaxLength);
        SkillLearningCluster? inserted = null;
        _clusters.TryInsertAsync(Arg.Do<SkillLearningCluster>(c => inserted = c), Arg.Any<CancellationToken>())
            .Returns(true);

        await _collector.CollectImplicitCorrectionAsync(new SkillLearningImplicitCorrection(
            AgentId, MessageNormalizer.Hash(longWish), excerpt, "user-1", "de", null, "[]", Guid.NewGuid()));

        inserted.ShouldNotBeNull();
        inserted!.ClusterKey.ShouldBe(MessageNormalizer.Hash(longWish));
        inserted.ClusterKey.ShouldNotBe(MessageNormalizer.Hash(excerpt));
    }

    // The negation detector matches on single words, so without the same floor the refusal path uses a
    // greeting answered with "nein" would open a cluster keyed on the greeting.
    [Test]
    public async Task AnImplicitCorrectionOnAVeryShortUtterance_IsNotACase()
    {
        await _collector.CollectImplicitCorrectionAsync(new SkillLearningImplicitCorrection(
            AgentId, MessageNormalizer.Hash("hallo Klacksy"), "hallo Klacksy", "user-1", "de", null, "[]", Guid.NewGuid()));

        await _cases.DidNotReceive().AddAsync(Arg.Any<SkillLearningCase>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnImplicitCorrectionOnADismissedCluster_IsNotCountedAgain()
    {
        var dismissed = ExistingCluster(occurrenceCount: 5);
        dismissed.Status = SkillLearningClusterStatuses.Dismissed;
        _clusters.FindByKeyAsync(AgentId, MessageNormalizer.Hash(Wish), Arg.Any<CancellationToken>())
            .Returns(dismissed);

        await _collector.CollectImplicitCorrectionAsync(new SkillLearningImplicitCorrection(
            AgentId, MessageNormalizer.Hash(Wish), Wish, "user-1", "de", null, "[]", Guid.NewGuid()));

        await _cases.DidNotReceive().AddAsync(Arg.Any<SkillLearningCase>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnImplicitCorrectionFailure_IsSwallowed()
    {
        _clusters.FindByKeyAsync(AgentId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<SkillLearningCluster?>>(_ => throw new InvalidOperationException("database down"));

        await Should.NotThrowAsync(() => _collector.CollectImplicitCorrectionAsync(
            new SkillLearningImplicitCorrection(
                AgentId, MessageNormalizer.Hash(Wish), Wish, "user-1", "de", null, "[]", Guid.NewGuid())));
    }

    private static SkillLearningCluster ExistingCluster(int occurrenceCount) => new()
    {
        Id = Guid.NewGuid(),
        AgentId = AgentId,
        ClusterKey = MessageNormalizer.Hash(Wish),
        Status = SkillLearningClusterStatuses.Collecting,
        OccurrenceCount = occurrenceCount,
        DistinctUserCount = 1
    };
}
