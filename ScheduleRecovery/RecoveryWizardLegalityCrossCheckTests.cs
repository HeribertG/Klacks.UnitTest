// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Controller;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.ScheduleRecovery.Engine;
using Klacks.ScheduleRecovery.Model;
using NUnit.Framework;
using Shouldly;
using static Klacks.UnitTest.ScheduleRecovery.RecoveryTestKit;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// Cross-engine seam tests. They pin that the two planning helpers now agree on what "legal" means: the
/// Recovery engine treats the hard caps (min-pause / max-consecutive-days / max-weekly-hours /
/// max-daily-hours) and shift-rotation as gates that exclude a candidate — the same rules the Wizard's
/// mutation gate (Stage0HardConstraintChecker) enforces. So a plan the Recovery engine produces passes
/// the Wizard's own Stage-0 gate: where no legal cover exists, Recovery leaves the critical slot
/// uncovered instead of committing a violation. Each test runs the real LocalRepairEngine, then feeds
/// the repaired plan into the real Stage0HardConstraintChecker and asserts it is clean.
///
/// Before the all-hard change (2026-06-24) these two cases documented the divergence (Recovery committed
/// what the Wizard vetoed); they now guard the closed seam — flip them back the moment Recovery starts
/// committing a hard violation again.
/// </summary>
[TestFixture]
public sealed class RecoveryWizardLegalityCrossCheckTests
{
    private readonly IRecoveryEngine _engine = new LocalRepairEngine();
    private readonly Stage0HardConstraintChecker _wizardGate = new();

    [Test]
    public void Positive_control_clean_in_group_cover_passes_both_engines()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "absent")
            .Agent(Agent(2), "candidate", maxConsecutiveDays: 6, minPauseHours: 11m)
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Deltas.Count.ShouldBe(1);
        proposal.Deltas[0].ToAgentId.ShouldBe(Agent(2));
        proposal.Objective.NewHardViolations.ShouldBe(0);

        var repaired = proposal.Deltas.Select(FromDelta).ToList();
        var context = ContextWith(d, d, CoreAgentFor(Agent(2), maxConsecutiveDays: 6, minRestHours: 11, maxDailyHours: 10));

        // Recovery covers cleanly AND the Wizard's hard gate agrees → the harness is not always-red.
        _wizardGate.ValidateScenario(repaired, context).ShouldBeNull();
    }

    [Test]
    public void MaxConsecutiveDays_recovery_refuses_so_the_repaired_plan_passes_stage0()
    {
        var d1 = Day(6, 1);
        var d2 = Day(6, 2);
        var d3 = Day(6, 3);
        var d4 = Day(6, 4);
        var s = Shift(1);

        // The only candidate already works three consecutive days (cap 3). Covering the absent agent's
        // day-4 slot would push it to four — a hard violation. Under the all-hard policy Recovery excludes
        // the candidate and leaves the critical slot uncovered, rather than committing the violation.
        var snapshot = new SnapshotBuilder()
            .Days(d1, d2, d3, d4)
            .Agent(Agent(1), "absent")
            .Agent(Agent(2), "candidate", maxConsecutiveDays: 3)
            .Work(Agent(2), d1, s, ShiftCategory.Early, At(d1, 8), At(d1, 16), 8m)
            .Work(Agent(2), d2, s, ShiftCategory.Early, At(d2, 8), At(d2, 16), 8m)
            .Work(Agent(2), d3, s, ShiftCategory.Early, At(d3, 8), At(d3, 16), 8m)
            .Work(Agent(1), d4, s, ShiftCategory.Early, At(d4, 8), At(d4, 16), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d4]), Ruleset.Default);

        // Recovery no longer commits the consecutive-days breach — the slot stays uncovered (seam closed).
        proposal.Deltas.ShouldBeEmpty();
        proposal.Objective.NewHardViolations.ShouldBe(0);
        proposal.Uncovered.ShouldContain(u => u.Date == d4 && u.IsCritical);

        // The plan Recovery produced (the candidate's three original days plus no illegal cover) is clean
        // by the Wizard's own Stage-0 gate.
        var repaired = new List<CoreToken>
        {
            Token(Agent(2), d1, At(d1, 8), At(d1, 16), 8m, s),
            Token(Agent(2), d2, At(d2, 8), At(d2, 16), 8m, s),
            Token(Agent(2), d3, At(d3, 8), At(d3, 16), 8m, s),
        };
        repaired.AddRange(proposal.Deltas.Select(FromDelta));

        var context = ContextWith(d1, d4, CoreAgentFor(Agent(2), maxConsecutiveDays: 3, minRestHours: 11, maxDailyHours: 10));

        _wizardGate.ValidateScenario(repaired, context).ShouldBeNull();
    }

    [Test]
    public void MaxDailyHours_recovery_refuses_so_the_repaired_plan_passes_stage0()
    {
        var d = Day(6, 3);
        var sMorning = Shift(1);
        var sLate = Shift(2);

        // The candidate already works a 6h morning shift and has a 10h daily cap. Covering the absent
        // agent's 8h late shift (a free afternoon window) would total 14h — over the cap. Under the
        // all-hard policy Recovery now knows the daily cap and excludes the candidate.
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "absent")
            .Agent(Agent(2), "candidate", maxDailyHours: 10m)
            .Work(Agent(2), d, sMorning, ShiftCategory.Early, At(d, 6), At(d, 12), 6m)
            .Work(Agent(1), d, sLate, ShiftCategory.Late, At(d, 14), At(d, 22), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        // Recovery is no longer blind to the daily cap — it refuses and leaves the slot uncovered.
        proposal.Deltas.ShouldBeEmpty();
        proposal.Objective.NewHardViolations.ShouldBe(0);
        proposal.Uncovered.ShouldContain(u => u.Date == d && u.IsCritical);

        var repaired = new List<CoreToken>
        {
            Token(Agent(2), d, At(d, 6), At(d, 12), 6m, sMorning),
        };
        repaired.AddRange(proposal.Deltas.Select(FromDelta));

        var context = ContextWith(d, d, CoreAgentFor(Agent(2), maxConsecutiveDays: 0, minRestHours: 2, maxDailyHours: 10));

        _wizardGate.ValidateScenario(repaired, context).ShouldBeNull();
    }

    private static CoreToken FromDelta(CellDelta delta)
        => Token(delta.ToAgentId, delta.Date, delta.StartAt, delta.EndAt, delta.Hours, delta.ShiftId ?? Guid.Empty);

    private static CoreToken Token(Guid agentId, DateOnly date, DateTime startAt, DateTime endAt, decimal hours, Guid shiftId)
        => new(
            WorkIds: [],
            ShiftTypeIndex: ShiftTypeInference.FromStartTime(TimeOnly.FromDateTime(startAt)),
            Date: date,
            TotalHours: hours,
            StartAt: startAt,
            EndAt: endAt,
            BlockId: Guid.Empty,
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: null,
            ShiftRefId: shiftId,
            AgentId: agentId.ToString());

    private static CoreAgent CoreAgentFor(Guid id, int maxConsecutiveDays, double minRestHours, double maxDailyHours)
        => new(
            Id: id.ToString(),
            CurrentHours: 0,
            GuaranteedHours: 0,
            MaxConsecutiveDays: maxConsecutiveDays,
            MinRestHours: minRestHours,
            Motivation: 0,
            MaxDailyHours: maxDailyHours,
            MaxWeeklyHours: 0,
            MaxOptimalGap: 0)
        {
            PerformsShiftWork = true,
            MaximumHours = 0,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
            WorkOnSaturday = true,
            WorkOnSunday = true,
        };

    private static CoreWizardContext ContextWith(DateOnly from, DateOnly until, params CoreAgent[] agents)
        => new()
        {
            PeriodFrom = from,
            PeriodUntil = until,
            Agents = agents,
        };
}
