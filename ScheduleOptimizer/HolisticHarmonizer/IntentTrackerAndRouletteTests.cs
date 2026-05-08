// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.HolisticHarmonizer.Llm;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Loop;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer;

[TestFixture]
public class IntentSuccessTrackerTests
{
    [Test]
    public void SuccessRate_NoData_ReturnsNeutralHalf()
    {
        var tracker = new IntentSuccessTracker();
        tracker.SuccessRate(HolisticIntent.ConsolidateBlock).ShouldBe(0.5, tolerance: 1e-9);
    }

    [Test]
    public void Note_AcceptedIncrementsAcceptCount()
    {
        var tracker = new IntentSuccessTracker();
        tracker.Note(HolisticIntent.ConsolidateBlock, BatchAcceptance.Accepted);
        tracker.Note(HolisticIntent.ConsolidateBlock, BatchAcceptance.PartiallyAccepted);

        var snapshot = tracker.Snapshot()[HolisticIntent.ConsolidateBlock];
        snapshot.Proposed.ShouldBe(2);
        snapshot.Accepted.ShouldBe(2);
        // Laplace: (2+1)/(2+2) = 0.75
        tracker.SuccessRate(HolisticIntent.ConsolidateBlock).ShouldBe(0.75, tolerance: 1e-9);
    }

    [Test]
    public void Note_RejectsAndDegradesCountAsLosses()
    {
        var tracker = new IntentSuccessTracker();
        tracker.Note(HolisticIntent.EnlargePause, BatchAcceptance.Rejected);
        tracker.Note(HolisticIntent.EnlargePause, BatchAcceptance.WouldDegrade);

        var snapshot = tracker.Snapshot()[HolisticIntent.EnlargePause];
        snapshot.Proposed.ShouldBe(2);
        snapshot.Accepted.ShouldBe(0);
        // Laplace: (0+1)/(2+2) = 0.25
        tracker.SuccessRate(HolisticIntent.EnlargePause).ShouldBe(0.25, tolerance: 1e-9);
    }

    [Test]
    public void Note_UnknownIntent_LazilyTracksIt()
    {
        var tracker = new IntentSuccessTracker(new[] { HolisticIntent.ConsolidateBlock });
        tracker.Note("custom_intent", BatchAcceptance.Accepted);
        tracker.Snapshot().ContainsKey("custom_intent").ShouldBeTrue();
        tracker.SuccessRate("custom_intent").ShouldBe(2.0 / 3.0, tolerance: 1e-9);
    }
}

[TestFixture]
public class RouletteIntentSelectorTests
{
    [Test]
    public void Pick_DeterministicWithSeededRandom()
    {
        var tracker = new IntentSuccessTracker();
        var selector = new RouletteIntentSelector(new Random(42));
        var first = selector.Pick(HolisticIntent.All, tracker);
        var second = new RouletteIntentSelector(new Random(42)).Pick(HolisticIntent.All, tracker);
        first.ShouldBe(second);
    }

    [Test]
    public void Pick_FavoursHighSuccessIntentAcrossManyDraws()
    {
        var tracker = new IntentSuccessTracker();
        // ConsolidateBlock: 9 wins, 0 losses → rate ~ 0.91
        for (var i = 0; i < 9; i++)
        {
            tracker.Note(HolisticIntent.ConsolidateBlock, BatchAcceptance.Accepted);
        }
        // EnlargePause + RedistributeLoad: 0 wins, 9 losses → rate ~ 0.09
        for (var i = 0; i < 9; i++)
        {
            tracker.Note(HolisticIntent.EnlargePause, BatchAcceptance.Rejected);
            tracker.Note(HolisticIntent.RedistributeLoad, BatchAcceptance.Rejected);
        }

        var selector = new RouletteIntentSelector(new Random(17));
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < 200; i++)
        {
            var pick = selector.Pick(HolisticIntent.All, tracker);
            counts[pick] = counts.GetValueOrDefault(pick) + 1;
        }

        // ConsolidateBlock should be picked clearly more often than the floor losers.
        counts[HolisticIntent.ConsolidateBlock].ShouldBeGreaterThan(counts.GetValueOrDefault(HolisticIntent.EnlargePause));
        counts[HolisticIntent.ConsolidateBlock].ShouldBeGreaterThan(counts.GetValueOrDefault(HolisticIntent.RedistributeLoad));
    }

    [Test]
    public void Pick_ZeroSuccessIntentStillSometimesSelected()
    {
        var tracker = new IntentSuccessTracker();
        for (var i = 0; i < 50; i++)
        {
            tracker.Note(HolisticIntent.ConsolidateBlock, BatchAcceptance.Accepted);
            tracker.Note(HolisticIntent.EnlargePause, BatchAcceptance.Rejected);
        }

        var selector = new RouletteIntentSelector(new Random(7));
        var enlargeCount = 0;
        for (var i = 0; i < 500; i++)
        {
            if (selector.Pick(HolisticIntent.All, tracker) == HolisticIntent.EnlargePause)
            {
                enlargeCount++;
            }
        }
        // Floor weight 0.05 keeps loser intents alive — expect at least a handful of picks.
        enlargeCount.ShouldBeGreaterThan(5);
    }

    [Test]
    public void Pick_EmptyIntentList_Throws()
    {
        var selector = new RouletteIntentSelector(new Random(1));
        Should.Throw<ArgumentException>(() => selector.Pick([], new IntentSuccessTracker()));
    }
}
