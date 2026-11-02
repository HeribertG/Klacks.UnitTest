// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleRecovery.Engine;
using Klacks.ScheduleRecovery.Model;
using NUnit.Framework;
using Shouldly;
using static Klacks.UnitTest.ScheduleRecovery.RecoveryTestKit;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// Golden masters for the lead/trail legality of a cover: the work a replacement takes over carries its briefing,
/// debriefing and travel time, so overlap, minimum rest, maximum hours and an absence on a day the work only reaches
/// through its night or its lead/trail are judged on the padded interval. Briefing and debriefing carry time but no
/// paid hours; only the extra paid hours (travel, corrections) count towards the caps.
/// </summary>
[TestFixture]
public sealed class LocalRepairEngineLeadTrailTests
{
    private static readonly DayAvailability Absent = new(true, false, true);

    private readonly IRecoveryEngine _engine = new LocalRepairEngine();

    private static RecoveryProposal Repair(RecoverySnapshot snapshot, params DateOnly[] dates)
        => new LocalRepairEngine().Repair(snapshot, new AbsenceEvent(Agent(1), dates), Ruleset.Default);

    [Test]
    public void A_night_cover_running_into_an_absence_of_the_next_day_is_not_placeable()
    {
        var d = Day(6, 3);
        var next = d.AddDays(1);
        var snapshot = new SnapshotBuilder()
            .Days(d, next)
            .Agent(Agent(1), "absent")
            .Agent(Agent(2), "candidate")
            .Work(Agent(1), d, Shift(1), ShiftCategory.Night, At(d, 22), At(next, 6), 8m)
            .Availability(Agent(2), next, Absent)
            .Build();

        var proposal = Repair(snapshot, d);

        proposal.Deltas.ShouldBeEmpty();
        proposal.Uncovered.ShouldContain(u => u.Date == d && u.IsCritical);
    }

    [Test]
    public void The_same_night_cover_is_placed_when_the_next_day_is_free()
    {
        var d = Day(6, 3);
        var next = d.AddDays(1);
        var snapshot = new SnapshotBuilder()
            .Days(d, next)
            .Agent(Agent(1), "absent")
            .Agent(Agent(2), "candidate")
            .Work(Agent(1), d, Shift(1), ShiftCategory.Night, At(d, 22), At(next, 6), 8m)
            .Build();

        var proposal = Repair(snapshot, d);

        proposal.Deltas.ShouldHaveSingleItem().ToAgentId.ShouldBe(Agent(2));
    }

    [Test]
    public void A_contractual_day_off_after_the_night_does_not_block_the_cover()
    {
        var d = Day(6, 3);
        var next = d.AddDays(1);
        var snapshot = new SnapshotBuilder()
            .Days(d, next)
            .Agent(Agent(1), "absent")
            .Agent(Agent(2), "candidate")
            .Work(Agent(1), d, Shift(1), ShiftCategory.Night, At(d, 22), At(next, 6), 8m)
            .Availability(Agent(2), next, new DayAvailability(false, false, false))
            .Build();

        Repair(snapshot, d).Deltas.ShouldHaveSingleItem().ToAgentId.ShouldBe(Agent(2));
    }

    [Test]
    public void A_cover_ending_exactly_at_midnight_does_not_reach_the_absence_of_the_next_day()
    {
        var d = Day(6, 3);
        var next = d.AddDays(1);
        var snapshot = new SnapshotBuilder()
            .Days(d, next)
            .Agent(Agent(1), "absent")
            .Agent(Agent(2), "candidate")
            .Work(Agent(1), d, Shift(1), ShiftCategory.Late, At(d, 16), At(next, 0), 8m)
            .Availability(Agent(2), next, Absent)
            .Build();

        Repair(snapshot, d).Deltas.ShouldHaveSingleItem().ToAgentId.ShouldBe(Agent(2));
    }

    [Test]
    public void A_trail_reaching_into_an_absence_of_the_next_day_makes_the_candidate_unplaceable()
    {
        var d = Day(6, 3);
        var next = d.AddDays(1);

        RecoverySnapshot Build(decimal trail) => new SnapshotBuilder()
            .Days(d, next)
            .Agent(Agent(1), "absent")
            .Agent(Agent(2), "candidate")
            .PaddedWork(Agent(1), d, Shift(1), ShiftCategory.Late, At(d, 15), At(d, 23), 8m, trailHours: trail)
            .Availability(Agent(2), next, Absent)
            .Build();

        Repair(Build(0m), d).Deltas.ShouldHaveSingleItem();
        var padded = Repair(Build(2m), d);
        padded.Deltas.ShouldBeEmpty();
        padded.Uncovered.ShouldContain(u => u.Date == d && u.IsCritical);
    }

    [Test]
    public void A_lead_reaching_into_an_absence_of_the_previous_day_makes_the_candidate_unplaceable()
    {
        var d = Day(6, 3);
        var previous = d.AddDays(-1);

        RecoverySnapshot Build(decimal lead) => new SnapshotBuilder()
            .Days(previous, d)
            .Agent(Agent(1), "absent")
            .Agent(Agent(2), "candidate")
            .PaddedWork(Agent(1), d, Shift(1), ShiftCategory.Early, At(d, 0, 30), At(d, 8, 30), 8m, leadHours: lead)
            .Availability(Agent(2), previous, Absent)
            .Build();

        Repair(Build(0m), d).Deltas.ShouldHaveSingleItem();
        Repair(Build(1m), d).Deltas.ShouldBeEmpty();
    }

    [Test]
    public void A_lead_overlapping_the_end_of_the_candidates_previous_work_makes_the_candidate_unplaceable()
    {
        var previous = Day(6, 2);
        var d = Day(6, 3);

        RecoverySnapshot Build(decimal lead) => new SnapshotBuilder()
            .Days(previous, d)
            .Agent(Agent(1), "absent")
            .Agent(Agent(2), "candidate")
            .Work(Agent(2), previous, Shift(2), ShiftCategory.Night, At(previous, 22), At(d, 7), 9m)
            .PaddedWork(Agent(1), d, Shift(1), ShiftCategory.Early, At(d, 8), At(d, 16), 8m, leadHours: lead)
            .Build();

        Repair(Build(0m), d).Deltas.ShouldHaveSingleItem();
        Repair(Build(1.5m), d).Deltas.ShouldBeEmpty();
    }

    [Test]
    public void A_trail_overlapping_the_start_of_the_candidates_next_work_makes_the_candidate_unplaceable()
    {
        var d = Day(6, 3);
        var next = d.AddDays(1);

        RecoverySnapshot Build(decimal trail) => new SnapshotBuilder()
            .Days(d, next)
            .Agent(Agent(1), "absent")
            .Agent(Agent(2), "candidate")
            .PaddedWork(Agent(1), d, Shift(1), ShiftCategory.Late, At(d, 16), At(d, 23), 7m, trailHours: trail)
            .Work(Agent(2), next, Shift(2), ShiftCategory.Early, At(next, 0, 30), At(next, 8), 7.5m)
            .Build();

        Repair(Build(0m), d).Deltas.ShouldHaveSingleItem();
        Repair(Build(2m), d).Deltas.ShouldBeEmpty();
    }

    [Test]
    public void The_lead_of_the_candidates_own_work_counts_as_occupied_time()
    {
        var d = Day(6, 3);
        var next = d.AddDays(1);

        RecoverySnapshot Build(decimal ownLead) => new SnapshotBuilder()
            .Days(d, next)
            .Agent(Agent(1), "absent")
            .Agent(Agent(2), "candidate")
            .Work(Agent(1), d, Shift(1), ShiftCategory.Night, At(d, 22), At(next, 7), 9m)
            .PaddedWork(Agent(2), next, Shift(2), ShiftCategory.Early, At(next, 8), At(next, 16), 8m, leadHours: ownLead)
            .Build();

        Repair(Build(0m), d).Deltas.ShouldHaveSingleItem();
        Repair(Build(2m), d).Deltas.ShouldBeEmpty();
    }

    [Test]
    public void The_minimum_rest_is_measured_from_the_start_of_the_lead()
    {
        var previous = Day(6, 2);
        var d = Day(6, 3);

        RecoverySnapshot Build(decimal lead) => new SnapshotBuilder()
            .Days(previous, d)
            .Agent(Agent(1), "absent")
            .Agent(Agent(2), "candidate", minPauseHours: 11m)
            .Work(Agent(2), previous, Shift(2), ShiftCategory.Late, At(previous, 12), At(previous, 20), 8m)
            .PaddedWork(Agent(1), d, Shift(1), ShiftCategory.Early, At(d, 7), At(d, 15), 8m, leadHours: lead)
            .Build();

        Repair(Build(0m), d).Deltas.ShouldHaveSingleItem();
        Repair(Build(1m), d).Deltas.ShouldBeEmpty();
    }

    [Test]
    public void The_minimum_rest_is_measured_to_the_end_of_the_trail()
    {
        var d = Day(6, 3);
        var next = d.AddDays(1);

        RecoverySnapshot Build(decimal trail) => new SnapshotBuilder()
            .Days(d, next)
            .Agent(Agent(1), "absent")
            .Agent(Agent(2), "candidate", minPauseHours: 11m)
            .PaddedWork(Agent(1), d, Shift(1), ShiftCategory.Late, At(d, 14), At(d, 22), 8m, trailHours: trail)
            .Work(Agent(2), next, Shift(2), ShiftCategory.Early, At(next, 9), At(next, 17), 8m)
            .Build();

        Repair(Build(0m), d).Deltas.ShouldHaveSingleItem();
        Repair(Build(1m), d).Deltas.ShouldBeEmpty();
    }

    [Test]
    public void Extra_paid_hours_count_towards_the_weekly_cap_but_a_briefing_lead_does_not()
    {
        var d = Day(6, 3);

        SnapshotBuilder WithWeek()
        {
            var builder = new SnapshotBuilder()
                .Days(Day(6, 1), Day(6, 2), d, Day(6, 4), Day(6, 5))
                .Agent(Agent(1), "absent")
                .Agent(Agent(2), "candidate", maxWeeklyHours: 40m);
            foreach (var day in new[] { Day(6, 1), Day(6, 2), Day(6, 4), Day(6, 5) })
            {
                builder.Work(Agent(2), day, Shift(2), ShiftCategory.Early, At(day, 8), At(day, 16), 8m);
            }
            return builder;
        }

        var exactly = WithWeek().PaddedWork(Agent(1), d, Shift(1), ShiftCategory.Early, At(d, 8), At(d, 16), 8m, leadHours: 1m).Build();
        var over = WithWeek().PaddedWork(Agent(1), d, Shift(1), ShiftCategory.Early, At(d, 8), At(d, 16), 8m, extraPaidHours: 1m).Build();

        Repair(exactly, d).Deltas.ShouldHaveSingleItem();
        Repair(over, d).Deltas.ShouldBeEmpty();
    }

    [Test]
    public void Extra_paid_hours_count_towards_the_period_cap()
    {
        var d = Day(6, 3);

        RecoverySnapshot Build(decimal extra) => new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "absent")
            .Agent(Agent(2), "candidate", maximumHours: 40m, currentPeriodHours: 30m)
            .PaddedWork(Agent(1), d, Shift(1), ShiftCategory.Early, At(d, 8), At(d, 16), 8m, extraPaidHours: extra)
            .Build();

        Repair(Build(1m), d).Deltas.ShouldHaveSingleItem();
        Repair(Build(3m), d).Deltas.ShouldBeEmpty();
    }

    [Test]
    public void Extra_paid_hours_count_towards_the_daily_cap()
    {
        var d = Day(6, 3);

        RecoverySnapshot Build(decimal extra) => new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "absent")
            .Agent(Agent(2), "candidate", maxDailyHours: 8m)
            .PaddedWork(Agent(1), d, Shift(1), ShiftCategory.Early, At(d, 8), At(d, 16), 8m, extraPaidHours: extra)
            .Build();

        Repair(Build(0m), d).Deltas.ShouldHaveSingleItem();
        Repair(Build(1m), d).Deltas.ShouldBeEmpty();
    }

    [Test]
    public void The_cover_delta_carries_the_lead_trail_and_extra_paid_hours()
    {
        var d = Day(6, 3);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "absent")
            .Agent(Agent(2), "candidate")
            .PaddedWork(Agent(1), d, Shift(1), ShiftCategory.Early, At(d, 8), At(d, 16), 8m,
                leadHours: 0.5m, trailHours: 0.25m, extraPaidHours: 1.5m)
            .Build();

        var delta = Repair(snapshot, d).Deltas.ShouldHaveSingleItem();

        delta.LeadHours.ShouldBe(0.5m);
        delta.TrailHours.ShouldBe(0.25m);
        delta.ExtraPaidHours.ShouldBe(1.5m);
        delta.Hours.ShouldBe(8m);
    }

    [Test]
    public void A_swap_does_not_hand_the_demand_to_a_pivot_with_an_absence_on_the_day_the_night_reaches()
    {
        var d = Day(6, 3);
        var next = d.AddDays(1);

        RecoverySnapshot Build(bool absentNext)
        {
            var builder = new SnapshotBuilder()
                .Days(d, next)
                .Agent(Agent(1), "absent")
                .Agent(Agent(2), "pivot")
                .Agent(Agent(3), "recipient")
                .Work(Agent(1), d, Shift(1), ShiftCategory.Night, At(d, 22), At(next, 6), 8m)
                .Work(Agent(2), d, Shift(2), ShiftCategory.Late, At(d, 18), At(d, 23), 5m)
                .Ineligible(Agent(3), Shift(1), d);
            if (absentNext)
            {
                builder.Availability(Agent(2), next, Absent);
            }
            return builder.Build();
        }

        Repair(Build(false), d).Deltas.Count.ShouldBe(2);
        var blocked = Repair(Build(true), d);
        blocked.Deltas.ShouldBeEmpty();
        blocked.Uncovered.ShouldContain(u => u.Date == d && u.IsCritical);
    }

    [Test]
    public void The_engine_is_deterministic_with_padded_works()
    {
        var d = Day(6, 3);
        var next = d.AddDays(1);
        var snapshot = new SnapshotBuilder()
            .Days(d, next)
            .Agent(Agent(1), "absent")
            .Agent(Agent(2), "a")
            .Agent(Agent(3), "b")
            .PaddedWork(Agent(1), d, Shift(1), ShiftCategory.Late, At(d, 15), At(d, 23), 8m, 1m, 2m, 0.5m)
            .Availability(Agent(2), next, Absent)
            .Build();

        Format(_engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default))
            .ShouldBe(Format(_engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default)));
    }
}
