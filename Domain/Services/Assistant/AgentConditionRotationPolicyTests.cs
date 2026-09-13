// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AgentConditionRotationPolicy -- the pure selection rule a ledger-tracked detector
/// uses instead of a fixed "oldest N" slice. Covers the two-group split (never opened before already
/// opened), the least-recently-observed ordering inside the opened group, the primary-order tiebreaker
/// in both groups, the cap, the uncapped case, and the empty input.
/// </summary>

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class AgentConditionRotationPolicyTests
{
    private static readonly DateTime BaseInstant = new(2026, 9, 13, 8, 0, 0, DateTimeKind.Utc);

    private sealed record Candidate(Guid Id, int Rank);

    private static readonly Comparison<Candidate> ByRank = (left, right) => left.Rank.CompareTo(right.Rank);

    private static Candidate MakeCandidate(int rank) => new(Guid.NewGuid(), rank);

    private static IReadOnlyList<Candidate> Select(
        IReadOnlyList<Candidate> candidates,
        IReadOnlyDictionary<Guid, DateTime> lastSeen,
        int cap) =>
        AgentConditionRotationPolicy.Select(candidates, candidate => candidate.Id, lastSeen, ByRank, cap);

    [Test]
    public void Select_NeverOpenedCandidates_ComeBeforeAlreadyOpenedOnes()
    {
        var alreadyOpened = MakeCandidate(1);
        var neverOpened = MakeCandidate(2);
        var lastSeen = new Dictionary<Guid, DateTime> { [alreadyOpened.Id] = BaseInstant };

        var selected = Select([alreadyOpened, neverOpened], lastSeen, cap: 2);

        selected.ShouldBe([neverOpened, alreadyOpened]);
    }

    [Test]
    public void Select_AlreadyOpenedCandidates_AreOrderedByLastSeenAscending()
    {
        var newest = MakeCandidate(1);
        var oldest = MakeCandidate(2);
        var middle = MakeCandidate(3);
        var lastSeen = new Dictionary<Guid, DateTime>
        {
            [newest.Id] = BaseInstant.AddHours(3),
            [oldest.Id] = BaseInstant,
            [middle.Id] = BaseInstant.AddHours(1)
        };

        var selected = Select([newest, oldest, middle], lastSeen, cap: 3);

        selected.ShouldBe([oldest, middle, newest]);
    }

    [Test]
    public void Select_TiesInBothGroups_FallBackToThePrimaryOrder()
    {
        var neverOpenedSecond = MakeCandidate(2);
        var neverOpenedFirst = MakeCandidate(1);
        var openedSecond = MakeCandidate(4);
        var openedFirst = MakeCandidate(3);
        var lastSeen = new Dictionary<Guid, DateTime>
        {
            [openedSecond.Id] = BaseInstant,
            [openedFirst.Id] = BaseInstant
        };

        var selected = Select(
            [openedSecond, neverOpenedSecond, openedFirst, neverOpenedFirst], lastSeen, cap: 4);

        selected.ShouldBe([neverOpenedFirst, neverOpenedSecond, openedFirst, openedSecond]);
    }

    [Test]
    public void Select_MoreCandidatesThanTheCap_KeepsOnlyTheFirstCapEntries()
    {
        var neverOpened = MakeCandidate(1);
        var longestUnobserved = MakeCandidate(2);
        var recentlyObserved = MakeCandidate(3);
        var lastSeen = new Dictionary<Guid, DateTime>
        {
            [longestUnobserved.Id] = BaseInstant,
            [recentlyObserved.Id] = BaseInstant.AddHours(5)
        };

        var selected = Select([recentlyObserved, longestUnobserved, neverOpened], lastSeen, cap: 2);

        selected.ShouldBe([neverOpened, longestUnobserved]);
    }

    [Test]
    public void Select_CapAtLeastAsLargeAsTheInput_ReturnsEveryCandidateInAStableOrder()
    {
        var third = MakeCandidate(3);
        var first = MakeCandidate(1);
        var second = MakeCandidate(2);
        var lastSeen = new Dictionary<Guid, DateTime>();

        var selected = Select([third, first, second], lastSeen, cap: 10);

        selected.ShouldBe([first, second, third]);
    }

    [Test]
    public void Select_EmptyCandidates_ReturnsEmpty()
    {
        var selected = Select([], new Dictionary<Guid, DateTime>(), cap: 50);

        selected.ShouldBeEmpty();
    }
}
