// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

/// <summary>
/// Pins the maximum weekly hours as a hard rule of Wizard 1: one week definition (ISO calendar week, Monday to Sunday),
/// paid hours of works counted on their start day, breaks never counted, locked tokens counted once and works outside
/// the plan (existing works in the period, boundary works) counted with their hours. 2026-06-01 is a Monday.
/// </summary>
[TestFixture]
public sealed class WeeklyHoursGuardTests
{
    private const string AgentId = "A";

    private const double WeeklyCap = 30;

    private const decimal SlotHours = 8;

    private static readonly DateOnly Monday = new(2026, 6, 1);

    private static readonly TimeOnly EarlyStart = new(6, 0);

    private static readonly TimeOnly EarlyEnd = new(14, 0);

    [Test]
    public void ExceedsCap_InSameIsoWeek_IsVetoed()
    {
        var thursday = Monday.AddDays(3);

        WeeklyHoursGuard.Violates(Agent(), thursday, SlotHours, EarlyRun(Monday, 3), Context()).ShouldBeTrue();
        FilterAccepts(thursday, EarlyRun(Monday, 3), Context()).ShouldBeFalse();
    }

    [Test]
    public void NextIsoWeek_IsNotCounted()
    {
        var nextMonday = Monday.AddDays(7);
        var previousSunday = Monday.AddDays(-1);

        WeeklyHoursGuard.Violates(Agent(), nextMonday, SlotHours, EarlyRun(Monday, 3), Context()).ShouldBeFalse();
        WeeklyHoursGuard.Violates(Agent(), previousSunday, SlotHours, EarlyRun(Monday, 3), Context()).ShouldBeFalse();
    }

    [Test]
    public void BreakHoursDoNotCountTowardWeeklyMax()
    {
        var wednesday = Monday.AddDays(2);
        var withBreaks = new CoreWizardContext
        {
            PeriodFrom = Monday,
            PeriodUntil = Monday.AddDays(27),
            SchedulingMaxDailyHours = 10,
            BreakBlockers = [new CoreBreakBlocker(AgentId, wednesday, wednesday.AddDays(1), "Vacation", SlotHours)],
        };
        var friday = Monday.AddDays(4);

        WeeklyHoursGuard.Violates(Agent(), friday, SlotHours, EarlyRun(Monday, 2), withBreaks).ShouldBeFalse(
            "16 h of work plus the 8 h slot stay under the 30 h cap; the 16 h of vacation must not count");
    }

    [Test]
    public void LockedTokenCountsOnce()
    {
        var lockedWork = LockedWork(Monday);
        var context = new CoreWizardContext
        {
            PeriodFrom = Monday,
            PeriodUntil = Monday.AddDays(27),
            SchedulingMaxDailyHours = 10,
            LockedWorks = [lockedWork],
        };
        var assigned = new List<CoreToken> { Early(Monday) with { IsLocked = true } };
        assigned.AddRange(EarlyRun(Monday.AddDays(1), 2));
        var thursday = Monday.AddDays(3);

        WeeklyHoursGuard.Violates(Agent(), thursday, 6m, assigned, context).ShouldBeFalse(
            "24 h of tokens (the locked one included) plus 6 h reach the cap of 30 h exactly; counting the locked work again would give 38 h");
    }

    [Test]
    public void BoundaryWorkInSameWeekCounts()
    {
        var periodStart = Monday.AddDays(2);
        var thursday = Monday.AddDays(3);
        var withBoundary = new CoreWizardContext
        {
            PeriodFrom = periodStart,
            PeriodUntil = periodStart.AddDays(27),
            SchedulingMaxDailyHours = 10,
            BoundaryLockedWorks = [LockedWork(Monday)],
            BoundaryExistingWorkBlockers = [Existing(Monday.AddDays(1))],
        };
        var withoutBoundary = new CoreWizardContext
        {
            PeriodFrom = periodStart,
            PeriodUntil = periodStart.AddDays(27),
            SchedulingMaxDailyHours = 10,
        };
        var assigned = EarlyRun(periodStart, 1);

        WeeklyHoursGuard.Violates(Agent(), thursday, SlotHours, assigned, withBoundary).ShouldBeTrue(
            "8 h locked on Monday + 8 h existing on Tuesday + 8 h on Wednesday + 8 h slot = 32 h > 30 h");
        WeeklyHoursGuard.Violates(Agent(), thursday, SlotHours, assigned, withoutBoundary).ShouldBeFalse();
    }

    [Test]
    public void InPeriodExistingWorkCountsWithItsHours()
    {
        var context = new CoreWizardContext
        {
            PeriodFrom = Monday,
            PeriodUntil = Monday.AddDays(27),
            SchedulingMaxDailyHours = 10,
            ExistingWorkBlockers = [Existing(Monday), Existing(Monday.AddDays(1))],
        };
        var thursday = Monday.AddDays(3);

        WeeklyHoursGuard.Violates(Agent(), thursday, SlotHours, EarlyRun(Monday.AddDays(2), 1), context).ShouldBeTrue();
    }

    [Test]
    public void AgentWithoutCap_FallsBackToTheSchedulingDefault_AndZeroMeansUnconstrained()
    {
        var thursday = Monday.AddDays(3);
        var agentWithoutCap = Agent() with { MaxWeeklyHours = 0 };
        var tightDefault = new CoreWizardContext
        {
            PeriodFrom = Monday,
            PeriodUntil = Monday.AddDays(27),
            SchedulingMaxDailyHours = 10,
            SchedulingMaxWeeklyHours = WeeklyCap,
        };
        var noDefault = new CoreWizardContext
        {
            PeriodFrom = Monday,
            PeriodUntil = Monday.AddDays(27),
            SchedulingMaxDailyHours = 10,
            SchedulingMaxWeeklyHours = 0,
        };

        WeeklyHoursGuard.Violates(agentWithoutCap, thursday, SlotHours, EarlyRun(Monday, 3), tightDefault).ShouldBeTrue();
        WeeklyHoursGuard.Violates(agentWithoutCap, thursday, SlotHours, EarlyRun(Monday, 3), noDefault).ShouldBeFalse();
    }

    private static bool FilterAccepts(DateOnly date, IReadOnlyList<CoreToken> assigned, CoreWizardContext context)
    {
        var slot = Early(date);
        return SlotConstraintFilter.IsValidAssignment(
            Agent(), date, 0, Guid.Empty, SlotHours, context, assigned, slot.StartAt, slot.EndAt);
    }

    private static CoreAgent Agent() => new(
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
        WorkOnMonday = true,
        WorkOnTuesday = true,
        WorkOnWednesday = true,
        WorkOnThursday = true,
        WorkOnFriday = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
    };

    private static CoreWizardContext Context() => new()
    {
        PeriodFrom = Monday.AddDays(-7),
        PeriodUntil = Monday.AddDays(27),
        SchedulingMaxDailyHours = 10,
    };

    private static List<CoreToken> EarlyRun(DateOnly first, int days)
        => Enumerable.Range(0, days).Select(offset => Early(first.AddDays(offset))).ToList();

    private static CoreToken Early(DateOnly date) => new(
        WorkIds: [],
        ShiftTypeIndex: 0,
        Date: date,
        TotalHours: SlotHours,
        StartAt: date.ToDateTime(EarlyStart),
        EndAt: date.ToDateTime(EarlyEnd),
        BlockId: Guid.NewGuid(),
        PositionInBlock: 0,
        IsLocked: false,
        LocationContext: null,
        ShiftRefId: Guid.Empty,
        AgentId: AgentId);

    private static CoreLockedWork LockedWork(DateOnly date) => new(
        WorkId: Guid.NewGuid().ToString(),
        AgentId: AgentId,
        Date: date,
        ShiftTypeIndex: 0,
        TotalHours: SlotHours,
        StartAt: date.ToDateTime(EarlyStart),
        EndAt: date.ToDateTime(EarlyEnd),
        ShiftRefId: Guid.NewGuid(),
        LocationContext: null);

    private static CoreExistingWorkBlocker Existing(DateOnly date)
        => new(AgentId, date, date.ToDateTime(EarlyStart), date.ToDateTime(EarlyEnd)) { Hours = SlotHours };
}
