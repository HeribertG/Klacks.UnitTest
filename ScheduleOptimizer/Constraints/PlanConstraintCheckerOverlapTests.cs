// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Constraints;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Constraints;

/// <summary>
/// Grill 2026-06-19 defect H1: the Wizard-1 Stage-0 hard-constraint gate (PlanConstraintChecker)
/// cannot represent a same-agent shift OVERLAP — there is no Overlap ViolationKind and the MinPause
/// check skips negative gaps (gapHours &gt;= 0 guard). A force-assigned double-booking can therefore
/// score FitnessStage0 = 0 and win selection. These tests assert the CORRECT behaviour (an overlap
/// must be a hard violation); they are RED until the fix adds overlap detection.
/// </summary>
[TestFixture]
public class PlanConstraintCheckerOverlapTests
{
    private static CoreAgent MakeAgent(string id) => new(
        Id: id,
        CurrentHours: 0,
        GuaranteedHours: 0,
        MaxConsecutiveDays: 6,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        MaximumHours = 0,
        PerformsShiftWork = true,
    };

    private static AssignmentView Assignment(string agentId, DateOnly date, TimeOnly start, TimeOnly end)
    {
        var hours = (decimal)(end - start).TotalHours;
        return new AssignmentView(
            AgentId: agentId,
            Date: date,
            ShiftRefId: Guid.NewGuid(),
            ShiftTypeIndex: 0,
            TotalHours: hours,
            StartAt: date.ToDateTime(start),
            EndAt: date.ToDateTime(end),
            BlockId: null,
            IsLocked: false);
    }

    private static CoreWizardContext Context(DateOnly date) => new()
    {
        Agents = [MakeAgent("A")],
        PeriodFrom = date,
        PeriodUntil = date,
        SchedulingMaxDailyHours = 10,
        SchedulingMinPauseHours = 11,
        SchedulingMaxConsecutiveDays = 6,
    };

    [Test]
    public void Check_SameDayOverlap_MustBeFlaggedAsHardViolation()
    {
        var date = new DateOnly(2026, 4, 20);
        // Two shifts on the SAME day for the SAME agent that overlap 11:00-12:00.
        // Daily sum 4h + 3h = 7h stays under the 10h cap so MaxDailyHours does not mask it.
        var a1 = Assignment("A", date, new TimeOnly(8, 0), new TimeOnly(12, 0));
        var a2 = Assignment("A", date, new TimeOnly(11, 0), new TimeOnly(14, 0));

        var result = new PlanConstraintChecker().Check([a1, a2], Context(date));

        result.ShouldContain(v => v.Kind == ViolationKind.Overlap,
            "a same-day double-booking (one human in two places 11:00-12:00) must be a hard Overlap violation (grill H1)");
    }

    [Test]
    public void Check_NegativeGapDoubleBooking_MustBeFlagged()
    {
        var date = new DateOnly(2026, 4, 20);
        // The later-starting shift begins BEFORE the earlier one ends -> negative gap.
        // MinPause's `gapHours >= 0` guard skips exactly this case. Hours kept under the 10h daily
        // cap (4h + 4h = 8h) so MaxDailyHours cannot accidentally mask the overlap.
        var earlier = Assignment("A", date, new TimeOnly(8, 0), new TimeOnly(12, 0));
        var laterOverlapping = Assignment("A", date, new TimeOnly(11, 0), new TimeOnly(15, 0));

        var result = new PlanConstraintChecker().Check([earlier, laterOverlapping], Context(date));

        result.ShouldContain(v => v.Kind == ViolationKind.Overlap,
            "a negative-gap double-booking (08:00-12:00 then 11:00-15:00, overlap 11:00-12:00) must be detected " +
            "even though the MinPause check guards on gapHours >= 0 (grill H1)");
    }
}
