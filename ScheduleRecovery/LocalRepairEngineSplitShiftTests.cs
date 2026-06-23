// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleRecovery.Engine;
using Klacks.ScheduleRecovery.Model;
using NUnit.Framework;
using Shouldly;
using static Klacks.UnitTest.ScheduleRecovery.RecoveryTestKit;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// Split-shift behaviour of the interval-based engine: an agent may hold several works on one day, so the
/// engine must cover every one of an absent agent's works (no silent drop) and must judge collisions by
/// time overlap, not day occupancy (a candidate free in a different window of the same day is a valid
/// direct cover).
/// </summary>
[TestFixture]
public sealed class LocalRepairEngineSplitShiftTests
{
    private readonly IRecoveryEngine _engine = new LocalRepairEngine();

    [Test]
    public void Both_works_of_a_split_shift_absence_are_covered()
    {
        var d = Day(6, 3);
        var morning = Shift(1);
        var afternoon = Shift(2);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B")
            .Agent(Agent(3), "C")
            .Work(Agent(1), d, morning, ShiftCategory.Early, At(d, 6), At(d, 14), 8m)
            .Work(Agent(1), d, afternoon, ShiftCategory.Late, At(d, 14), At(d, 22), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Uncovered.ShouldBeEmpty();
        proposal.Deltas.Count.ShouldBe(2);
        proposal.Deltas.Select(x => x.ShiftId).ShouldBe(new Guid?[] { morning, afternoon }, ignoreOrder: true);
    }

    [Test]
    public void Candidate_free_in_a_different_window_covers_directly_without_a_swap()
    {
        var d = Day(6, 3);
        var afternoon = Shift(1);
        var candidateMorning = Shift(2);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B")
            .Work(Agent(1), d, afternoon, ShiftCategory.Late, At(d, 14), At(d, 22), 8m)
            .Work(Agent(2), d, candidateMorning, ShiftCategory.Early, At(d, 6), At(d, 12), 6m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        var delta = proposal.Deltas.Single();
        delta.ShiftId.ShouldBe(afternoon);
        delta.ToAgentId.ShouldBe(Agent(2));
        delta.Tier.ShouldBe(EscalationTier.InGroupFree);
        proposal.Uncovered.ShouldBeEmpty();
    }

    [Test]
    public void Overlapping_same_day_work_still_blocks_a_direct_cover()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var overlapping = Shift(2);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B")
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Work(Agent(2), d, overlapping, ShiftCategory.Late, At(d, 12), At(d, 20), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Deltas.ShouldBeEmpty();
        proposal.Uncovered.Single().Reason.ShouldBe(RecoveryReasons.NoEligibleCandidate);
    }
}
