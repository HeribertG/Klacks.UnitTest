// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

/// <summary>
/// Pins what each rung of the coverage escalation buys. Relaxing the rest days is the ordinary price
/// of coverage and leaves the MaxWorkDays ideal standing — the finished plan is measured on package
/// length, no later operator shortens a package again and the only fitness term that sees block
/// length sits in stage 4, far below the rank at which the lexicographic comparison could reject it.
/// The widest rung drops the ideal as well, because a slot that can only be staffed on the sixth
/// consecutive day is a coverage question, and coverage is the highest rule of the specification
/// while the 5/2 ideal is not. What no rung buys is a hard rule: the MaxConsecutiveDays cap vetoes on
/// all three of them.
/// </summary>
[TestFixture]
public sealed class SlotConstraintFilterBlockIdealTests
{
    private const int SoftCapDays = 5;

    private const int HardCapDays = 6;

    private const decimal SlotHours = 8;

    private const string AgentId = "A";

    private static CoreAgent MakeAgent(int maxWorkDays = SoftCapDays) => new(
        Id: AgentId,
        CurrentHours: 0,
        GuaranteedHours: 160,
        MaxConsecutiveDays: HardCapDays,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        FullTime = 160,
        MaxWorkDays = maxWorkDays,
        MinRestDays = 2,
        PerformsShiftWork = true,
        WorkOnMonday = true,
        WorkOnTuesday = true,
        WorkOnWednesday = true,
        WorkOnThursday = true,
        WorkOnFriday = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
    };

    private static CoreWizardContext MakeContext() => new()
    {
        PeriodFrom = new DateOnly(2026, 6, 1),
        PeriodUntil = new DateOnly(2026, 6, 30),
        SchedulingMaxConsecutiveDays = HardCapDays,
        SchedulingMaxDailyHours = 10,
    };

    private static CoreToken MakeToken(DateOnly date) => new(
        WorkIds: [],
        ShiftTypeIndex: 0,
        Date: date,
        TotalHours: SlotHours,
        StartAt: date.ToDateTime(new TimeOnly(8, 0)),
        EndAt: date.ToDateTime(new TimeOnly(16, 0)),
        BlockId: Guid.NewGuid(),
        PositionInBlock: 0,
        IsLocked: false,
        LocationContext: null,
        ShiftRefId: Guid.Empty,
        AgentId: AgentId);

    private static List<CoreToken> Run(DateOnly first, int days)
    {
        var tokens = new List<CoreToken>();
        for (var offset = 0; offset < days; offset++)
        {
            tokens.Add(MakeToken(first.AddDays(offset)));
        }

        return tokens;
    }

    [Test]
    public void RelaxedRestDays_StillRejectTheDayThatWouldExceedTheBlockIdeal()
    {
        var agent = MakeAgent();
        var context = MakeContext();
        var assigned = Run(new DateOnly(2026, 6, 1), SoftCapDays);
        var sixthDay = new DateOnly(2026, 6, 6);

        SlotConstraintFilter.IsValidAssignment(
            agent, sixthDay, 0, Guid.Empty, SlotHours, context, assigned, relaxation: SlotRelaxation.RestDaysOnly)
            .ShouldBeFalse();
    }

    [Test]
    public void StrictFilter_RejectsTheDayThatWouldExceedTheBlockIdeal()
    {
        var agent = MakeAgent();
        var context = MakeContext();
        var assigned = Run(new DateOnly(2026, 6, 1), SoftCapDays);
        var sixthDay = new DateOnly(2026, 6, 6);

        SlotConstraintFilter.IsValidAssignment(
            agent, sixthDay, 0, Guid.Empty, SlotHours, context, assigned)
            .ShouldBeFalse();
    }

    [Test]
    public void RelaxedRestDays_AcceptTheDayTheRestDayRuleAloneVetoed()
    {
        var agent = MakeAgent();
        var context = MakeContext();
        var assigned = Run(new DateOnly(2026, 6, 1), 2);
        var afterOneFreeDay = new DateOnly(2026, 6, 4);

        SlotConstraintFilter.IsValidAssignment(
            agent, afterOneFreeDay, 0, Guid.Empty, SlotHours, context, assigned)
            .ShouldBeFalse();

        SlotConstraintFilter.IsValidAssignment(
            agent, afterOneFreeDay, 0, Guid.Empty, SlotHours, context, assigned, relaxation: SlotRelaxation.RestDaysOnly)
            .ShouldBeTrue();
    }

    [Test]
    public void WidestRung_AcceptsTheDayOnlyTheBlockIdealVetoed()
    {
        var agent = MakeAgent();
        var context = MakeContext();
        var assigned = Run(new DateOnly(2026, 6, 1), SoftCapDays);
        var sixthDay = new DateOnly(2026, 6, 6);

        SlotConstraintFilter.IsValidAssignment(
            agent, sixthDay, 0, Guid.Empty, SlotHours, context, assigned,
            relaxation: SlotRelaxation.All)
            .ShouldBeTrue();
    }

    [Test]
    public void WidestRung_StillRejectsTheDayTheHardConsecutiveCapVetoes()
    {
        var agent = MakeAgent();
        var context = MakeContext();
        var assigned = Run(new DateOnly(2026, 6, 1), HardCapDays);
        var seventhDay = new DateOnly(2026, 6, 7);

        SlotConstraintFilter.IsValidAssignment(
            agent, seventhDay, 0, Guid.Empty, SlotHours, context, assigned,
            relaxation: SlotRelaxation.All)
            .ShouldBeFalse();
    }

    [Test]
    public void AgentWithoutABlockIdeal_IsOnlyBoundByTheHardConsecutiveCap()
    {
        var agent = MakeAgent(maxWorkDays: 0);
        var context = MakeContext();
        var sixDays = Run(new DateOnly(2026, 6, 1), HardCapDays);

        SlotConstraintFilter.IsValidAssignment(
            agent, new DateOnly(2026, 6, 6), 0, Guid.Empty, SlotHours, context, Run(new DateOnly(2026, 6, 1), SoftCapDays))
            .ShouldBeTrue();

        SlotConstraintFilter.IsValidAssignment(
            agent, new DateOnly(2026, 6, 7), 0, Guid.Empty, SlotHours, context, sixDays)
            .ShouldBeFalse();
    }
}
