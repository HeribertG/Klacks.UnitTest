// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Controller;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

/// <summary>
/// Pins the weekly rest-day veto of Wizard 1. The schedule check counts rest days per Monday-to-Sunday
/// week and treats every day a work touches as a work day, so the wizard must refuse any slot after
/// which such a week keeps fewer than MinRestDays — on every rung of the coverage escalation, in the
/// auction's hard stage and across the period boundary. 2026-06-01 is a Monday.
/// </summary>
[TestFixture]
public sealed class WeeklyRestDayGuardTests
{
    private const string AgentId = "A";

    private const int RestDays = 2;

    private const int HardCapDays = 6;

    private const int SoftCapDays = 5;

    private const decimal SlotHours = 8;

    private const string WeeklyRestDaysRule = "WeeklyRestDays";

    private static readonly DateOnly Monday = new(2026, 6, 1);

    private static readonly TimeOnly EarlyStart = new(6, 0);

    private static readonly TimeOnly EarlyEnd = new(14, 0);

    private static readonly TimeOnly NightStart = new(22, 0);

    [Test]
    public void SixthDayInsideOneCalendarWeek_IsRejectedEvenOnTheWidestRung()
    {
        var assigned = EarlyRun(Monday, SoftCapDays);
        var saturday = Monday.AddDays(5);

        FilterAccepts(saturday, assigned, Context(), SlotRelaxation.All).ShouldBeFalse();
    }

    [Test]
    public void SixDayBlockAcrossTwoCalendarWeeks_IsAcceptedOnTheWidestRung()
    {
        var thursday = Monday.AddDays(3);
        var assigned = EarlyRun(thursday, SoftCapDays);
        var tuesday = thursday.AddDays(SoftCapDays);

        FilterAccepts(tuesday, assigned, Context(), SlotRelaxation.All).ShouldBeTrue();
    }

    /// <summary>
    /// Owner rule 2026-09-30: the night Monday 22:00 to Tuesday 06:00 is followed by a free Wednesday and
    /// by 48 hours of free block until Thursday 06:00 - the package rest of two days - so Tuesday is a
    /// rest day and an early shift on Sunday still leaves Tuesday and Wednesday free. With a shift on
    /// Wednesday instead, Tuesday is no longer followed by a free day and becomes a work day.
    /// </summary>
    [Test]
    public void NightSpilloverDay_IsRestOnlyWithAFreeNextDayAndAFullFreeBlock()
    {
        var assigned = new List<CoreToken> { Night(Monday) };
        assigned.AddRange(EarlyRun(Monday.AddDays(3), 3));
        var sunday = Monday.AddDays(6);

        FilterAccepts(sunday, assigned, Context()).ShouldBeTrue();

        var withWednesday = new List<CoreToken> { Night(Monday) };
        withWednesday.AddRange(EarlyRun(Monday.AddDays(2), 3));
        var saturday = Monday.AddDays(5);
        WeeklyRestDayGuard.Violates(
            Agent(), saturday, withWednesday, Context(), Early(saturday).StartAt, Early(saturday).EndAt)
            .ShouldBeTrue();
    }

    [Test]
    public void SameWeekWithoutTheSpillover_AcceptsTheSundaySlot()
    {
        var assigned = EarlyRun(Monday, 1);
        assigned.AddRange(EarlyRun(Monday.AddDays(3), 3));
        var sunday = Monday.AddDays(6);

        FilterAccepts(sunday, assigned, Context()).ShouldBeTrue();
    }

    [Test]
    public void NightSlotSpillingIntoTheNextWeek_IsJudgedInBothWeeks()
    {
        var nextMonday = Monday.AddDays(7);
        var sunday = Monday.AddDays(6);

        WeeklyRestDayGuard.Violates(
            Agent(), sunday, EarlyRun(nextMonday.AddDays(1), SoftCapDays), Context(),
            sunday.ToDateTime(NightStart), nextMonday.ToDateTime(EarlyStart))
            .ShouldBeTrue();
        WeeklyRestDayGuard.Violates(
            Agent(), sunday, EarlyRun(nextMonday.AddDays(2), SoftCapDays), Context(),
            sunday.ToDateTime(NightStart), nextMonday.ToDateTime(EarlyStart))
            .ShouldBeFalse();
    }

    [Test]
    public void WeekCrossingThePeriodStart_CountsTheBoundaryWorksOfThePreviousMonth()
    {
        var periodStart = new DateOnly(2026, 7, 1);
        var boundaryMonday = periodStart.AddDays(-2);
        var boundary = new List<CoreLockedWork>
        {
            BoundaryLocked(boundaryMonday),
            BoundaryLocked(boundaryMonday.AddDays(1)),
        };
        var assigned = EarlyRun(periodStart, 3);
        var saturday = periodStart.AddDays(3);

        var withoutBoundary = Context(periodStart, periodStart.AddDays(30));
        var contextWithBoundary = new CoreWizardContext
        {
            PeriodFrom = periodStart,
            PeriodUntil = periodStart.AddDays(30),
            SchedulingMaxConsecutiveDays = HardCapDays,
            SchedulingMaxDailyHours = 10,
            BoundaryLockedWorks = boundary,
        };

        WeeklyRestDayGuard.Violates(Agent(), saturday, assigned, contextWithBoundary, Early(saturday).StartAt, Early(saturday).EndAt)
            .ShouldBeTrue();
        WeeklyRestDayGuard.Violates(Agent(), saturday, assigned, withoutBoundary,Early(saturday).StartAt, Early(saturday).EndAt)
            .ShouldBeFalse();
    }

    [Test]
    public void WeekCrossingThePeriodEnd_CountsTheExistingWorksOfTheNextMonth()
    {
        var periodEnd = Monday.AddDays(2);
        var context = new CoreWizardContext
        {
            PeriodFrom = Monday.AddDays(-28),
            PeriodUntil = periodEnd,
            SchedulingMaxConsecutiveDays = HardCapDays,
            SchedulingMaxDailyHours = 10,
            BoundaryExistingWorkBlockers =
            [
                BoundaryExisting(periodEnd.AddDays(1)),
                BoundaryExisting(periodEnd.AddDays(2)),
                BoundaryExisting(periodEnd.AddDays(3)),
            ],
        };
        var assigned = EarlyRun(Monday, 2);

        WeeklyRestDayGuard.Violates(Agent(), periodEnd, assigned, context, Early(periodEnd).StartAt, Early(periodEnd).EndAt)
            .ShouldBeTrue();
    }

    [Test]
    public void BreakOnlyDay_CountsAsRest()
    {
        var saturday = Monday.AddDays(5);
        var context = new CoreWizardContext
        {
            PeriodFrom = Monday,
            PeriodUntil = Monday.AddDays(27),
            SchedulingMaxConsecutiveDays = HardCapDays,
            SchedulingMaxDailyHours = 10,
            BreakBlockers = [new CoreBreakBlocker(AgentId, saturday, saturday, "Vacation")],
        };
        var assigned = EarlyRun(Monday, 4);
        var friday = Monday.AddDays(4);

        FilterAccepts(friday, assigned, context).ShouldBeTrue();
    }

    [Test]
    public void SlotOnADayAlreadyWorked_IsNeverRejectedEvenInAShortWeek()
    {
        var lockedWeek = Enumerable.Range(0, 7)
            .Select(offset => BoundaryLocked(Monday.AddDays(offset)))
            .ToList();
        var context = new CoreWizardContext
        {
            PeriodFrom = Monday,
            PeriodUntil = Monday.AddDays(27),
            SchedulingMaxConsecutiveDays = HardCapDays,
            SchedulingMaxDailyHours = 10,
            LockedWorks = lockedWeek,
        };
        var wednesday = Monday.AddDays(2);

        WeeklyRestDayGuard.Violates(
            Agent(), wednesday, [], context,
            wednesday.ToDateTime(new TimeOnly(16, 0)), wednesday.ToDateTime(new TimeOnly(20, 0)))
            .ShouldBeFalse();
    }

    [Test]
    public void AgentWithoutMinRestDays_IsNeverRejected()
    {
        var agent = Agent() with { MinRestDays = 0 };
        var saturday = Monday.AddDays(5);

        WeeklyRestDayGuard.Violates(agent, saturday, EarlyRun(Monday, SoftCapDays), Context(), Early(saturday).StartAt, Early(saturday).EndAt)
            .ShouldBeFalse();
    }

    [Test]
    public void AuctionHardStage_VetoesTheSixthDayInsideOneWeek()
    {
        var saturday = Monday.AddDays(5);
        var slot = new CoreShift(
            Guid.NewGuid().ToString(), "FD", saturday.ToString("yyyy-MM-dd"), "06:00", "14:00", (double)SlotHours, 1, 0);

        var verdict = new Stage0HardConstraintChecker().Check(Agent(), slot, EarlyRun(Monday, SoftCapDays), Context());

        verdict.ShouldNotBeNull();
        verdict!.Stage.ShouldBe(0);
        verdict.RuleName.ShouldBe(WeeklyRestDaysRule);
    }

    [Test]
    public void AuctionHardStage_AcceptsTheSixthDayAcrossTwoWeeks()
    {
        var thursday = Monday.AddDays(3);
        var tuesday = thursday.AddDays(SoftCapDays);
        var slot = new CoreShift(
            Guid.NewGuid().ToString(), "FD", tuesday.ToString("yyyy-MM-dd"), "06:00", "14:00", (double)SlotHours, 1, 0);

        new Stage0HardConstraintChecker().Check(Agent(), slot, EarlyRun(thursday, SoftCapDays), Context())
            .ShouldBeNull();
    }

    private static bool FilterAccepts(
        DateOnly date,
        IReadOnlyList<CoreToken> assigned,
        CoreWizardContext context,
        SlotRelaxation relaxation = SlotRelaxation.None)
    {
        var slot = Early(date);
        return SlotConstraintFilter.IsValidAssignment(
            Agent(), date, 0, Guid.Empty, SlotHours, context, assigned, slot.StartAt, slot.EndAt, relaxation);
    }

    private static CoreAgent Agent() => new(
        Id: AgentId,
        CurrentHours: 0,
        GuaranteedHours: 0,
        MaxConsecutiveDays: HardCapDays,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 60,
        MaxOptimalGap: 2)
    {
        MaxWorkDays = SoftCapDays,
        MinRestDays = RestDays,
        PerformsShiftWork = true,
        WorkOnMonday = true,
        WorkOnTuesday = true,
        WorkOnWednesday = true,
        WorkOnThursday = true,
        WorkOnFriday = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
    };

    private static CoreWizardContext Context() => Context(Monday, Monday.AddDays(27));

    private static CoreWizardContext Context(DateOnly from, DateOnly until) => new()
    {
        PeriodFrom = from,
        PeriodUntil = until,
        SchedulingMaxConsecutiveDays = HardCapDays,
        SchedulingMaxDailyHours = 10,
    };

    private static List<CoreToken> EarlyRun(DateOnly first, int days)
        => Enumerable.Range(0, days).Select(offset => Early(first.AddDays(offset))).ToList();

    private static CoreToken Early(DateOnly date)
        => Token(date, date.ToDateTime(EarlyStart), date.ToDateTime(EarlyEnd), 0);

    private static CoreToken Night(DateOnly date)
        => Token(date, date.ToDateTime(NightStart), date.AddDays(1).ToDateTime(EarlyStart), 2);

    private static CoreToken Token(DateOnly date, DateTime start, DateTime end, int shiftTypeIndex) => new(
        WorkIds: [],
        ShiftTypeIndex: shiftTypeIndex,
        Date: date,
        TotalHours: SlotHours,
        StartAt: start,
        EndAt: end,
        BlockId: Guid.NewGuid(),
        PositionInBlock: 0,
        IsLocked: false,
        LocationContext: null,
        ShiftRefId: Guid.Empty,
        AgentId: AgentId);

    private static CoreLockedWork BoundaryLocked(DateOnly date) => new(
        WorkId: Guid.NewGuid().ToString(),
        AgentId: AgentId,
        Date: date,
        ShiftTypeIndex: 0,
        TotalHours: SlotHours,
        StartAt: date.ToDateTime(EarlyStart),
        EndAt: date.ToDateTime(EarlyEnd),
        ShiftRefId: Guid.NewGuid(),
        LocationContext: null);

    private static CoreExistingWorkBlocker BoundaryExisting(DateOnly date)
        => new(AgentId, date, date.ToDateTime(EarlyStart), date.ToDateTime(EarlyEnd));
}
