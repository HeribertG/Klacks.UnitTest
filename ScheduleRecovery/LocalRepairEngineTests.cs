// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleRecovery.Engine;
using Klacks.ScheduleRecovery.Model;
using NUnit.Framework;
using Shouldly;
using static Klacks.UnitTest.ScheduleRecovery.RecoveryTestKit;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// Golden-master tests for the in-group local-repair engine: exact deltas for tier-0 covers, tier-1 swap
/// chains, the leave-uncovered fallback, the ported find_replacement ranking, the coverage-dominated
/// legality tier and run-to-run determinism. Every test is fully in-memory with fixed Guids.
/// </summary>
[TestFixture]
public sealed class LocalRepairEngineTests
{
    private readonly IRecoveryEngine _engine = new LocalRepairEngine();

    [Test]
    public void Absence_without_working_cell_yields_empty_proposal()
    {
        var d = Day(6, 3);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B")
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Deltas.ShouldBeEmpty();
        proposal.Uncovered.ShouldBeEmpty();
        proposal.Objective.ShouldBe(RecoveryObjective.Zero);
    }

    [Test]
    public void Free_replacement_produces_exact_direct_delta_with_guid_tie_break()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B")
            .Agent(Agent(3), "C")
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Deltas.Count.ShouldBe(1);
        var delta = proposal.Deltas[0];
        delta.ShiftId.ShouldBe(s);
        delta.Date.ShouldBe(d);
        delta.FromAgentId.ShouldBe(Agent(1));
        delta.ToAgentId.ShouldBe(Agent(2));
        delta.Tier.ShouldBe(EscalationTier.InGroupFree);
        proposal.Uncovered.ShouldBeEmpty();
        proposal.Objective.ShouldBe(new RecoveryObjective(0, 0, 1, 1));
        proposal.HighestTier.ShouldBe(EscalationTier.InGroupFree);
    }

    [Test]
    public void Preferred_candidate_wins_over_lower_guid()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B")
            .Agent(Agent(3), "C", preferredShiftIds: [s])
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Deltas.Single().ToAgentId.ShouldBe(Agent(3));
    }

    [Test]
    public void Higher_target_hours_deficit_wins_when_clean_and_unpreferred()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B", targetHoursDeficit: 0m)
            .Agent(Agent(3), "C", targetHoursDeficit: 10m)
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Deltas.Single().ToAgentId.ShouldBe(Agent(3));
    }

    [Test]
    public void Swap_chain_relocates_blocker_then_covers()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var s2 = Shift(2);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B")
            .Agent(Agent(3), "C")
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Work(Agent(2), d, s2, ShiftCategory.Late, At(d, 14), At(d, 22), 8m)
            .Ineligible(Agent(3), s, d)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Deltas.Count.ShouldBe(2);

        var relocation = proposal.Deltas[0];
        relocation.ShiftId.ShouldBe(s2);
        relocation.FromAgentId.ShouldBe(Agent(2));
        relocation.ToAgentId.ShouldBe(Agent(3));
        relocation.Tier.ShouldBe(EscalationTier.InGroupSwap);

        var cover = proposal.Deltas[1];
        cover.ShiftId.ShouldBe(s);
        cover.FromAgentId.ShouldBe(Agent(1));
        cover.ToAgentId.ShouldBe(Agent(2));
        cover.Tier.ShouldBe(EscalationTier.InGroupSwap);

        proposal.Uncovered.ShouldBeEmpty();
        proposal.Objective.ShouldBe(new RecoveryObjective(0, 0, 8, 2));
        proposal.HighestTier.ShouldBe(EscalationTier.InGroupSwap);
    }

    [Test]
    public void Swap_is_blocked_when_recipient_is_ineligible_for_the_displaced_shift()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var s2 = Shift(2);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B")
            .Agent(Agent(3), "C")
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Work(Agent(2), d, s2, ShiftCategory.Late, At(d, 14), At(d, 22), 8m)
            .Ineligible(Agent(3), s, d)
            .Ineligible(Agent(3), s2, d)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Deltas.ShouldBeEmpty();
        proposal.Uncovered.Single().Reason.ShouldBe(RecoveryReasons.NoEligibleCandidate);
    }

    [Test]
    public void Locked_candidate_work_is_not_used_as_a_swap_displacement()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var s2 = Shift(2);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B")
            .Agent(Agent(3), "C")
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Work(Agent(2), d, s2, ShiftCategory.Late, At(d, 14), At(d, 22), 8m, locked: true)
            .Ineligible(Agent(3), s, d)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Deltas.ShouldBeEmpty();
        proposal.Uncovered.Single().Reason.ShouldBe(RecoveryReasons.NoEligibleCandidate);
    }

    [Test]
    public void Clean_direct_cover_is_preferred_over_swap()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var s2 = Shift(2);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B")
            .Agent(Agent(3), "C")
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Work(Agent(2), d, s2, ShiftCategory.Late, At(d, 14), At(d, 22), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Deltas.Count.ShouldBe(1);
        proposal.Deltas[0].ToAgentId.ShouldBe(Agent(3));
        proposal.Deltas[0].Tier.ShouldBe(EscalationTier.InGroupFree);
        proposal.HighestTier.ShouldBe(EscalationTier.InGroupFree);
    }

    [Test]
    public void Locked_slot_of_absent_agent_is_left_uncovered_for_manual_review()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B")
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m, locked: true)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Deltas.ShouldBeEmpty();
        var slot = proposal.Uncovered.Single();
        slot.ShiftId.ShouldBe(s);
        slot.Reason.ShouldBe(RecoveryReasons.Locked);
        slot.IsCritical.ShouldBeTrue();
        proposal.Objective.ShouldBe(new RecoveryObjective(1, 0, 0, 0));
        proposal.HighestTier.ShouldBe(EscalationTier.Uncovered);
    }

    [Test]
    public void Non_critical_slot_is_left_uncovered_when_covering_would_perturb()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B")
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .NonCritical(Agent(1), d)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Deltas.ShouldBeEmpty();
        var slot = proposal.Uncovered.Single();
        slot.Reason.ShouldBe(RecoveryReasons.NonCritical);
        slot.IsCritical.ShouldBeFalse();
        proposal.Objective.ShouldBe(RecoveryObjective.Zero);
    }

    [Test]
    public void Critical_slot_without_eligible_candidate_is_reported_uncovered()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B", blacklistedShiftIds: [s])
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Deltas.ShouldBeEmpty();
        var slot = proposal.Uncovered.Single();
        slot.Reason.ShouldBe(RecoveryReasons.NoEligibleCandidate);
        slot.IsCritical.ShouldBeTrue();
        proposal.Objective.ShouldBe(new RecoveryObjective(1, 0, 0, 0));
    }

    [Test]
    public void Blacklisted_candidate_is_excluded_in_favor_of_an_eligible_one()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B", blacklistedShiftIds: [s])
            .Agent(Agent(3), "C")
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Deltas.Single().ToAgentId.ShouldBe(Agent(3));
    }

    [Test]
    public void Unavailable_candidate_is_excluded_in_favor_of_an_available_one()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B")
            .Agent(Agent(3), "C")
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Unavailable(Agent(2), d)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Deltas.Single().ToAgentId.ShouldBe(Agent(3));
    }

    [Test]
    public void Weekly_hours_violation_makes_a_clean_candidate_rank_higher()
    {
        var monday = MondayOf(Day(6, 3));
        var tuesday = monday.AddDays(1);
        var s = Shift(1);
        var existing = Shift(2);
        var snapshot = new SnapshotBuilder()
            .Days(monday, tuesday)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B", maxWeeklyHours: 10m)
            .Agent(Agent(3), "C")
            .Work(Agent(1), tuesday, s, ShiftCategory.Early, At(tuesday, 8), At(tuesday, 16), 8m)
            .Work(Agent(2), monday, existing, ShiftCategory.Early, At(monday, 8), At(monday, 16), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [tuesday]), Ruleset.Default);

        proposal.Deltas.Single().ToAgentId.ShouldBe(Agent(3));
        proposal.Objective.NewHardViolations.ShouldBe(0);
    }

    [Test]
    public void Consecutive_days_violation_makes_a_clean_candidate_rank_higher()
    {
        var d = Day(6, 3);
        var dm1 = d.AddDays(-1);
        var dm2 = d.AddDays(-2);
        var s = Shift(1);
        var b1 = Shift(2);
        var b2 = Shift(3);
        var snapshot = new SnapshotBuilder()
            .Days(dm2, dm1, d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B", maxConsecutiveDays: 2)
            .Agent(Agent(3), "C")
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Work(Agent(2), dm2, b1, ShiftCategory.Early, At(dm2, 8), At(dm2, 16), 8m)
            .Work(Agent(2), dm1, b2, ShiftCategory.Early, At(dm1, 8), At(dm1, 16), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Deltas.Single().ToAgentId.ShouldBe(Agent(3));
        proposal.Objective.NewHardViolations.ShouldBe(0);
    }

    [Test]
    public void Min_pause_violation_makes_a_clean_candidate_rank_higher()
    {
        var d = Day(6, 3);
        var dm1 = d.AddDays(-1);
        var s = Shift(1);
        var b1 = Shift(2);
        var snapshot = new SnapshotBuilder()
            .Days(dm1, d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B", minPauseHours: 11m)
            .Agent(Agent(3), "C")
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 6), At(d, 14), 8m)
            .Work(Agent(2), dm1, b1, ShiftCategory.Late, At(dm1, 15), At(dm1, 23), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Deltas.Single().ToAgentId.ShouldBe(Agent(3));
        proposal.Objective.NewHardViolations.ShouldBe(0);
    }

    [Test]
    public void Covering_with_a_violation_beats_leaving_a_critical_slot_uncovered()
    {
        var monday = MondayOf(Day(6, 3));
        var tuesday = monday.AddDays(1);
        var s = Shift(1);
        var existing = Shift(2);
        var snapshot = new SnapshotBuilder()
            .Days(monday, tuesday)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B", maxWeeklyHours: 10m)
            .Work(Agent(1), tuesday, s, ShiftCategory.Early, At(tuesday, 8), At(tuesday, 16), 8m)
            .Work(Agent(2), monday, existing, ShiftCategory.Early, At(monday, 8), At(monday, 16), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [tuesday]), Ruleset.Default);

        proposal.Uncovered.ShouldBeEmpty();
        var delta = proposal.Deltas.Single();
        delta.ToAgentId.ShouldBe(Agent(2));
        proposal.Objective.UncoveredCritical.ShouldBe(0);
        proposal.Objective.NewHardViolations.ShouldBe(1);
    }

    [Test]
    public void Weekly_cap_is_detected_across_the_iso_year_boundary()
    {
        var lastYear = new DateOnly(2025, 12, 30);
        var thisYear = new DateOnly(2026, 1, 2);
        var s = Shift(1);
        var existing = Shift(2);
        var snapshot = new SnapshotBuilder()
            .Days(lastYear, thisYear)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B", maxWeeklyHours: 10m)
            .Work(Agent(1), thisYear, s, ShiftCategory.Early, At(thisYear, 8), At(thisYear, 16), 8m)
            .Work(Agent(2), lastYear, existing, ShiftCategory.Early, At(lastYear, 8), At(lastYear, 16), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [thisYear]), Ruleset.Default);

        proposal.Deltas.Single().ToAgentId.ShouldBe(Agent(2));
        proposal.Objective.NewHardViolations.ShouldBe(1);
    }

    [Test]
    public void Repair_is_deterministic_across_runs_for_a_swap_scenario()
    {
        var snapshot = SwapScenario();
        var absence = new AbsenceEvent(Agent(1), [Day(6, 3)]);

        var first = _engine.Repair(snapshot, absence, Ruleset.Default);
        var second = _engine.Repair(snapshot, absence, Ruleset.Default);

        Format(first).ShouldBe(Format(second));
    }

    [Test]
    public void Repair_is_deterministic_for_a_multi_demand_scenario()
    {
        var d0 = Day(6, 3);
        var d1 = Day(6, 4);
        var snapshot = new SnapshotBuilder()
            .Days(d0, d1)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B")
            .Agent(Agent(3), "C")
            .Work(Agent(1), d0, Shift(1), ShiftCategory.Early, At(d0, 6), At(d0, 14), 8m)
            .Work(Agent(1), d1, Shift(2), ShiftCategory.Late, At(d1, 14), At(d1, 22), 8m)
            .Build();
        var absence = new AbsenceEvent(Agent(1), [d0, d1]);

        var first = _engine.Repair(snapshot, absence, Ruleset.Default);
        var second = _engine.Repair(snapshot, absence, Ruleset.Default);

        Format(first).ShouldBe(Format(second));
        first.Deltas.Count.ShouldBe(2);
    }

    [Test]
    public void Repair_is_independent_of_agent_input_order()
    {
        var d = Day(6, 3);
        var s = Shift(1);

        var ascending = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B")
            .Agent(Agent(3), "C")
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Build();

        var descending = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(3), "C")
            .Agent(Agent(2), "B")
            .Agent(Agent(1), "A")
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Build();

        var absence = new AbsenceEvent(Agent(1), [d]);

        var fromAscending = _engine.Repair(ascending, absence, Ruleset.Default);
        var fromDescending = _engine.Repair(descending, absence, Ruleset.Default);

        Format(fromAscending).ShouldBe(Format(fromDescending));
        fromAscending.Deltas.Single().ToAgentId.ShouldBe(Agent(2));
    }

    private static RecoverySnapshot SwapScenario()
    {
        var d = Day(6, 3);
        return new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B")
            .Agent(Agent(3), "C")
            .Work(Agent(1), d, Shift(1), ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Work(Agent(2), d, Shift(2), ShiftCategory.Late, At(d, 14), At(d, 22), 8m)
            .Ineligible(Agent(3), Shift(1), d)
            .Build();
    }
}
