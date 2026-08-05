// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Constraints;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Constraints;

/// <summary>
/// CheckOverlap only compares the plan against itself, so an assignment colliding with a work already
/// stored in the database scored zero hard violations - the plan looked clean while the agent was
/// booked twice in reality. The bitmap engine feeds those very works in as the cells being scored, so
/// the same check must not report them against themselves; that is what the block-id-plus-containment
/// exclusion is for, and the last two tests pin both sides of it.
/// </summary>
[TestFixture]
public sealed class PlanConstraintCheckerExistingWorkTests
{
    private const string AgentId = "A";
    private static readonly DateOnly Day = new(2026, 4, 13);

    [Test]
    public void Check_PlannedTokenOverlappingAnExistingWork_IsAHardViolation()
    {
        var context = Context(blockers: [Blocker(new TimeOnly(6, 0), new TimeOnly(14, 0))]);

        var violations = new PlanConstraintChecker().Check(
            [TokenAssignment(new TimeOnly(12, 0), new TimeOnly(20, 0))], context);

        violations.ShouldContain(v => v.Kind == ViolationKind.ExistingWorkOverlap);
    }

    [Test]
    public void Check_PlannedTokenNextToAnExistingWork_IsClean()
    {
        var context = Context(blockers: [Blocker(new TimeOnly(0, 0), new TimeOnly(6, 0))]);

        var violations = new PlanConstraintChecker().Check(
            [TokenAssignment(new TimeOnly(18, 0), new TimeOnly(23, 0))], context);

        violations.ShouldNotContain(v => v.Kind == ViolationKind.ExistingWorkOverlap);
    }

    [Test]
    public void Check_PlannedTokenTooCloseToAnExistingWork_ViolatesMinPause()
    {
        var context = Context(blockers: [Blocker(new TimeOnly(0, 0), new TimeOnly(6, 0))]);

        var violations = new PlanConstraintChecker().Check(
            [TokenAssignment(new TimeOnly(10, 0), new TimeOnly(18, 0))], context);

        violations.ShouldContain(v => v.Kind == ViolationKind.MinPauseHours);
        violations.ShouldNotContain(v => v.Kind == ViolationKind.ExistingWorkOverlap);
    }

    [Test]
    public void Check_BoundaryLockedNightShift_BlocksTheFirstMorningOfThePeriod()
    {
        var context = Context(boundaryLocked:
            [
                new CoreLockedWork(
                    WorkId: "w1",
                    AgentId: AgentId,
                    Date: Day.AddDays(-1),
                    ShiftTypeIndex: 2,
                    TotalHours: 8m,
                    StartAt: Day.AddDays(-1).ToDateTime(new TimeOnly(22, 0)),
                    EndAt: Day.ToDateTime(new TimeOnly(6, 0)),
                    ShiftRefId: Guid.NewGuid(),
                    LocationContext: null),
            ]);

        var violations = new PlanConstraintChecker().Check(
            [TokenAssignment(new TimeOnly(5, 0), new TimeOnly(13, 0))], context);

        violations.ShouldContain(v => v.Kind == ViolationKind.ExistingWorkOverlap);
    }

    [Test]
    public void Check_BitmapCellIsTheExistingWorkItself_IsNotReportedAgainstItself()
    {
        var context = Context(blockers: [Blocker(new TimeOnly(8, 0), new TimeOnly(16, 0))]);

        var violations = new PlanConstraintChecker().Check(
            [BitmapAssignment(new TimeOnly(8, 0), new TimeOnly(16, 0))], context);

        violations.ShouldNotContain(v => v.Kind == ViolationKind.ExistingWorkOverlap);
    }

    [Test]
    public void Check_TokenWithTheSameSpanAsAnExistingWork_IsStillAViolation()
    {
        // A GA token is a NEW work, so an identical span is a real double booking. Only the bitmap
        // adapter - recognisable by the missing block id - may match itself.
        var context = Context(blockers: [Blocker(new TimeOnly(8, 0), new TimeOnly(16, 0))]);

        var violations = new PlanConstraintChecker().Check(
            [TokenAssignment(new TimeOnly(8, 0), new TimeOnly(16, 0))], context);

        violations.ShouldContain(v => v.Kind == ViolationKind.ExistingWorkOverlap);
    }

    [Test]
    public void Check_WithoutAnyExternalWork_BehavesAsBefore()
    {
        var violations = new PlanConstraintChecker().Check(
            [TokenAssignment(new TimeOnly(8, 0), new TimeOnly(16, 0))], Context());

        violations.ShouldNotContain(v => v.Kind == ViolationKind.ExistingWorkOverlap);
    }

    private static CoreExistingWorkBlocker Blocker(TimeOnly start, TimeOnly end) => new(
        AgentId, Day, Day.ToDateTime(start), Day.ToDateTime(end));

    private static AssignmentView TokenAssignment(TimeOnly start, TimeOnly end)
        => Assignment(start, end, Guid.NewGuid());

    private static AssignmentView BitmapAssignment(TimeOnly start, TimeOnly end)
        => Assignment(start, end, null);

    private static AssignmentView Assignment(TimeOnly start, TimeOnly end, Guid? blockId) => new(
        AgentId: AgentId,
        Date: Day,
        ShiftRefId: Guid.NewGuid(),
        ShiftTypeIndex: 0,
        TotalHours: (decimal)(end - start).TotalHours,
        StartAt: Day.ToDateTime(start),
        EndAt: Day.ToDateTime(end),
        BlockId: blockId,
        IsLocked: false);

    private static CoreWizardContext Context(
        IReadOnlyList<CoreExistingWorkBlocker>? blockers = null,
        IReadOnlyList<CoreLockedWork>? boundaryLocked = null) => new()
    {
        ExistingWorkBlockers = blockers ?? [],
        BoundaryLockedWorks = boundaryLocked ?? [],
        Agents =
        [
            new CoreAgent(
                Id: AgentId,
                CurrentHours: 0,
                GuaranteedHours: 0,
                MaxConsecutiveDays: 6,
                MinRestHours: 11,
                Motivation: 0.5,
                MaxDailyHours: 24,
                MaxWeeklyHours: 60,
                MaxOptimalGap: 2)
            {
                MaximumHours = 0,
                PerformsShiftWork = true,
            },
        ],
        PeriodFrom = Day,
        PeriodUntil = Day,
        SchedulingMaxDailyHours = 24,
        SchedulingMinPauseHours = 11,
        SchedulingMaxConsecutiveDays = 6,
    };
}
