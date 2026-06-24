// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleRecovery.Engine;
using Klacks.ScheduleRecovery.Model;
using NUnit.Framework;
using Shouldly;
using static Klacks.UnitTest.ScheduleRecovery.RecoveryTestKit;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// Golden masters for the MaximumHours (period contract cap) gate. A cover that would push a candidate's
/// planned period hours (baseline plus what this repair already added) past the cap is excluded — and the
/// cap holds cumulatively across multiple covers to the same agent in one absence.
/// </summary>
[TestFixture]
public sealed class LocalRepairEngineMaximumHoursTests
{
    private readonly IRecoveryEngine _engine = new LocalRepairEngine();

    [Test]
    public void A_single_cover_that_would_breach_the_period_cap_is_excluded()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        // Candidate is at 36h of a 40h cap; an 8h cover would reach 44h → excluded → slot stays uncovered.
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "absent")
            .Agent(Agent(2), "candidate", maximumHours: 40m, currentPeriodHours: 36m)
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Deltas.ShouldBeEmpty();
        proposal.Uncovered.ShouldContain(u => u.Date == d && u.IsCritical);
    }

    [Test]
    public void The_period_cap_holds_cumulatively_across_two_covers_to_the_same_agent()
    {
        var d1 = Day(6, 3);
        var d2 = Day(6, 4);
        var s = Shift(1);
        // Candidate at 30h of a 40h cap. First 8h cover (→38h) fits; the second (→46h) would breach the cap
        // once the first is accounted for → only day 1 is covered, day 2 stays uncovered.
        var snapshot = new SnapshotBuilder()
            .Days(d1, d2)
            .Agent(Agent(1), "absent")
            .Agent(Agent(2), "candidate", maximumHours: 40m, currentPeriodHours: 30m)
            .Work(Agent(1), d1, s, ShiftCategory.Early, At(d1, 8), At(d1, 16), 8m)
            .Work(Agent(1), d2, s, ShiftCategory.Early, At(d2, 8), At(d2, 16), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d1, d2]), Ruleset.Default);

        proposal.Deltas.Count.ShouldBe(1);
        proposal.Deltas[0].Date.ShouldBe(d1);
        proposal.Deltas[0].ToAgentId.ShouldBe(Agent(2));
        proposal.Uncovered.ShouldContain(u => u.Date == d2 && u.IsCritical);
    }

    [Test]
    public void A_cover_within_the_period_cap_is_committed()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "absent")
            .Agent(Agent(2), "candidate", maximumHours: 40m, currentPeriodHours: 20m)
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Uncovered.ShouldBeEmpty();
        proposal.Deltas.ShouldHaveSingleItem().ToAgentId.ShouldBe(Agent(2));
    }
}
