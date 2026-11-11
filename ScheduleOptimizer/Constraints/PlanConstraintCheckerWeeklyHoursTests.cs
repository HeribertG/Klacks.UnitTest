// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Constraints;

/// <summary>
/// The maximum weekly hours as a plan-level legality defect: a week is the ISO calendar week, only a week holding a plan
/// assignment is reported, and the in-period existing works count only when the caller passes them as streak works
/// (Wizard-1 path) - on the bitmap path they are the scored cells themselves. 2026-06-01 is a Monday.
/// </summary>
[TestFixture]
public sealed class PlanConstraintCheckerWeeklyHoursTests
{
    private const string AgentId = "A";

    private const double WeeklyCap = 30;

    private const decimal ShiftHours = 8;

    private static readonly DateOnly Monday = new(2026, 6, 1);

    [Test]
    public void MaxWeeklyHours_ReportsWeekWithPlanDay()
    {
        var thisWeek = Enumerable.Range(0, 4).Select(offset => Assignment(Monday.AddDays(offset))).ToList();
        var nextWeek = Enumerable.Range(7, 2).Select(offset => Assignment(Monday.AddDays(offset)));

        var violations = new PlanConstraintChecker().Check([.. thisWeek, .. nextWeek], Context());

        var weekly = violations.Where(v => v.Kind == ViolationKind.MaxWeeklyHours).ToList();
        weekly.Count.ShouldBe(1, "32 h in the first week exceed 30 h, the 16 h of the next week do not");
        weekly[0].AgentId.ShouldBe(AgentId);
        weekly[0].Date.ShouldBe(Monday, "the violation points at a plan day so the repair finds a token to remove");
    }

    [Test]
    public void MaxWeeklyHours_PointsAtTheFirstUnlockedAssignment()
    {
        var assignments = new List<AssignmentView> { Assignment(Monday) with { IsLocked = true } };
        assignments.AddRange(Enumerable.Range(1, 3).Select(offset => Assignment(Monday.AddDays(offset))));

        var violations = new PlanConstraintChecker().Check(assignments, Context());

        violations.Single(v => v.Kind == ViolationKind.MaxWeeklyHours).Date.ShouldBe(Monday.AddDays(1));
    }

    [Test]
    public void ExistingWorksOnlyViaStreakWorks()
    {
        var existing = new List<CoreExistingWorkBlocker> { Existing(Monday.AddDays(2)), Existing(Monday.AddDays(3)) };
        var context = Context(existing);
        var plan = new List<AssignmentView> { Assignment(Monday), Assignment(Monday.AddDays(1)) };

        new PlanConstraintChecker().Check(plan, context)
            .ShouldNotContain(v => v.Kind == ViolationKind.MaxWeeklyHours, "bitmap path: the existing works are cells, not extra hours");
        new PlanConstraintChecker().Check(plan, context, context.ExistingWorkBlockers)
            .ShouldContain(v => v.Kind == ViolationKind.MaxWeeklyHours, "Wizard-1 path: 16 h plan + 16 h existing = 32 h > 30 h");
    }

    [Test]
    public void WeekOfExistingWorksAlone_IsNotReported()
    {
        var nextWeekExisting = Enumerable.Range(7, 5).Select(offset => Existing(Monday.AddDays(offset))).ToList();
        var context = Context(nextWeekExisting);

        new PlanConstraintChecker().Check([Assignment(Monday)], context, context.ExistingWorkBlockers)
            .ShouldNotContain(v => v.Kind == ViolationKind.MaxWeeklyHours, "a week without a plan assignment is nothing the plan could change");
    }

    private static AssignmentView Assignment(DateOnly date) => new(
        AgentId: AgentId,
        Date: date,
        ShiftRefId: Guid.NewGuid(),
        ShiftTypeIndex: 0,
        TotalHours: ShiftHours,
        StartAt: date.ToDateTime(new TimeOnly(8, 0)),
        EndAt: date.ToDateTime(new TimeOnly(16, 0)),
        BlockId: Guid.NewGuid(),
        IsLocked: false);

    private static CoreExistingWorkBlocker Existing(DateOnly date)
        => new(AgentId, date, date.ToDateTime(new TimeOnly(8, 0)), date.ToDateTime(new TimeOnly(16, 0))) { Hours = ShiftHours };

    private static CoreWizardContext Context(IReadOnlyList<CoreExistingWorkBlocker>? existing = null) => new()
    {
        ExistingWorkBlockers = existing ?? [],
        Agents =
        [
            new CoreAgent(
                Id: AgentId,
                CurrentHours: 0,
                GuaranteedHours: 0,
                MaxConsecutiveDays: 6,
                MinRestHours: 11,
                Motivation: 0.5,
                MaxDailyHours: 10,
                MaxWeeklyHours: WeeklyCap,
                MaxOptimalGap: 2)
            {
                PerformsShiftWork = true,
                WorkOnSaturday = true,
                WorkOnSunday = true,
            },
        ],
        PeriodFrom = Monday,
        PeriodUntil = Monday.AddDays(27),
        SchedulingMaxDailyHours = 10,
        SchedulingMinPauseHours = 11,
        SchedulingMaxConsecutiveDays = 6,
    };
}
