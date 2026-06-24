// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleRecovery.Engine;
using Klacks.ScheduleRecovery.Model;
using NUnit.Framework;
using Shouldly;
using static Klacks.UnitTest.ScheduleRecovery.RecoveryTestKit;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// Golden masters for the swap-path collision gate. The depth-2 swap relocates a blocked agent's
/// overlapping work and lets the blocked agent take the demand. The blocked agent must be able to host
/// the demand without a residual collision — in particular a cross-midnight neighbour on the previous
/// day, which the single-day "displaced" scan never sees. Before the gate, such a swap was built and the
/// blocked agent was double-booked (a physically impossible plan that CountViolations did not catch).
/// </summary>
[TestFixture]
public sealed class LocalRepairEngineSwapCollisionTests
{
    private readonly IRecoveryEngine _engine = new LocalRepairEngine();

    [Test]
    public void Swap_is_blocked_when_pivot_has_a_cross_midnight_neighbour_overlapping_the_demand()
    {
        var dPrev = Day(6, 2);
        var d = Day(6, 3);
        var sDemand = Shift(1);
        var sOther = Shift(2);
        var sNight = Shift(3);

        // No direct cover exists: the pivot (A2) cannot host the early demand directly (its own day-d work
        // and its previous-night tail both overlap), and the recipient (A3) is ineligible for the demand
        // shift. That forces the swap path. A2's night shift dPrev 22:00 -> d 07:00 overlaps the demand
        // (d 05:00-13:00) — so after relocating A2's day-d work, A2 still cannot legally hold the demand.
        var snapshot = new SnapshotBuilder()
            .Days(dPrev, d)
            .Agent(Agent(1), "absent")
            .Agent(Agent(2), "pivot")
            .Agent(Agent(3), "recipient")
            .Work(Agent(2), dPrev, sNight, ShiftCategory.Night, At(dPrev, 22), At(d, 7), 9m)
            .Work(Agent(2), d, sOther, ShiftCategory.Early, At(d, 6), At(d, 14), 8m)
            .Work(Agent(1), d, sDemand, ShiftCategory.Early, At(d, 5), At(d, 13), 8m)
            .Ineligible(Agent(3), sDemand, d)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        // The only safe outcome is to leave the critical slot uncovered — never a double-booked swap.
        proposal.Deltas.ShouldBeEmpty();
        proposal.Uncovered.Count.ShouldBe(1);
        proposal.Uncovered[0].ShiftId.ShouldBe(sDemand);
        proposal.Uncovered[0].Date.ShouldBe(d);
        proposal.Uncovered[0].IsCritical.ShouldBeTrue();
        proposal.Objective.UncoveredCritical.ShouldBe(1);
    }

    [Test]
    public void Swap_still_succeeds_when_the_pivot_has_no_colliding_neighbour()
    {
        var d = Day(6, 3);
        var sDemand = Shift(1);
        var sOther = Shift(2);

        // Same shape as above WITHOUT the cross-midnight neighbour: the gate must NOT fire. The pivot (A2)
        // relocates its day-d work to the recipient (A3) and takes the demand.
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "absent")
            .Agent(Agent(2), "pivot")
            .Agent(Agent(3), "recipient")
            .Work(Agent(2), d, sOther, ShiftCategory.Early, At(d, 6), At(d, 14), 8m)
            .Work(Agent(1), d, sDemand, ShiftCategory.Early, At(d, 5), At(d, 13), 8m)
            .Ineligible(Agent(3), sDemand, d)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Uncovered.ShouldBeEmpty();
        proposal.Deltas.Count.ShouldBe(2);
        proposal.Deltas.ShouldContain(x => x.ToAgentId == Agent(2) && x.ShiftId == sDemand);
        proposal.Deltas.ShouldContain(x => x.ToAgentId == Agent(3) && x.ShiftId == sOther);
        proposal.HighestTier.ShouldBe(EscalationTier.InGroupSwap);
    }
}
