// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Constraints;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Constraints;

/// <summary>
/// Stage-0 side of the 2026-08-14 blacklist hardening: a plan carrying an assignment on a
/// blacklisted shift must surface a ShiftBlacklistViolation — before this, no ViolationKind
/// covered the blacklist at all, so crossover children and externally built plans passed the
/// legality gate with blacklisted tokens on board.
/// </summary>
[TestFixture]
public sealed class PlanConstraintCheckerShiftBlacklistTests
{
    private static readonly Guid NightShift = Guid.NewGuid();
    private static readonly Guid EarlyShift = Guid.NewGuid();

    private static CoreAgent MakeAgent(string id) => new(
        Id: id,
        CurrentHours: 0,
        GuaranteedHours: 160,
        MaxConsecutiveDays: 6,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        FullTime = 160,
        MaxWorkDays = 5,
        MinRestDays = 2,
        PerformsShiftWork = true,
        WorkOnMonday = true,
        WorkOnTuesday = true,
        WorkOnWednesday = true,
        WorkOnThursday = true,
        WorkOnFriday = true,
    };

    private static CoreWizardContext MakeContext(params CoreShiftPreference[] preferences) => new()
    {
        PeriodFrom = new DateOnly(2026, 6, 1),
        PeriodUntil = new DateOnly(2026, 6, 30),
        SchedulingMaxConsecutiveDays = 6,
        SchedulingMaxDailyHours = 10,
        Agents = [MakeAgent("A")],
        ShiftPreferences = preferences,
    };

    private static AssignmentView MakeAssignment(string agentId, Guid shiftRefId, int shiftTypeIndex)
    {
        var monday = new DateOnly(2026, 6, 1);
        return new AssignmentView(
            AgentId: agentId,
            Date: monday,
            ShiftRefId: shiftRefId,
            ShiftTypeIndex: shiftTypeIndex,
            TotalHours: 8,
            StartAt: monday.ToDateTime(new TimeOnly(23, 0)),
            EndAt: monday.AddDays(1).ToDateTime(new TimeOnly(7, 0)),
            BlockId: null,
            IsLocked: false);
    }

    [Test]
    public void Check_AssignmentOnBlacklistedShift_SurfacesTheViolation()
    {
        var ctx = MakeContext(new CoreShiftPreference("A", NightShift, ShiftPreferenceKind.Blacklist));

        var violations = new PlanConstraintChecker()
            .Check([MakeAssignment("A", NightShift, 2)], ctx)
            .Where(v => v.Kind == ViolationKind.ShiftBlacklistViolation)
            .ToList();

        violations.Count.ShouldBe(1);
        violations[0].AgentId.ShouldBe("A");
    }

    [Test]
    public void Check_UnlistedShiftAndPreferredKind_SurfaceNothing()
    {
        var ctx = MakeContext(
            new CoreShiftPreference("A", NightShift, ShiftPreferenceKind.Blacklist),
            new CoreShiftPreference("A", EarlyShift, ShiftPreferenceKind.Preferred));

        var violations = new PlanConstraintChecker()
            .Check([MakeAssignment("A", EarlyShift, 0)], ctx)
            .Where(v => v.Kind == ViolationKind.ShiftBlacklistViolation);

        violations.ShouldBeEmpty();
    }
}
