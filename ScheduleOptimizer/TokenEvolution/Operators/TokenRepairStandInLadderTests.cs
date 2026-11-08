// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Constraints;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Operators;

/// <summary>
/// The repair ladder with a stand-in (placeholder) in the roster: the real person walks the ladder first, so a real person
/// goes past the soft block ideal (MaxWorkDays) before a stand-in takes the slot; a hard rule (the consecutive-days cap)
/// still leaves the slot to the stand-in. The real person stands on a locked five-day package, so no relocation can free
/// it and only the rung decides.
/// </summary>
[TestFixture]
public sealed class TokenRepairStandInLadderTests
{
    private const string RealId = "real";
    private const string StandInId = "stand-in";
    private const int SoftCapDays = 5;
    private const int HardCapDays = 6;
    private const decimal SlotHours = 8;
    private const string DateFormat = "yyyy-MM-dd";

    private static readonly DateOnly FirstDay = new(2026, 6, 4);

    [Test]
    public void Repair_RealPersonPastTheBlockIdeal_TakesTheSlotBeforeTheStandIn()
    {
        var (context, scenario) = Stage(HardCapDays);

        var result = new TokenRepair(new TokenConstraintChecker())
            .Apply(new TokenOperatorContext(scenario, null, context, new Random(0)));

        result.Tokens.ShouldContain(t => t.Date == FirstDay.AddDays(SoftCapDays) && t.AgentId == RealId);
        result.Tokens.ShouldNotContain(t => t.AgentId == StandInId);
    }

    [Test]
    public void Repair_RealPersonAtTheHardCap_LeavesTheSlotToTheStandIn()
    {
        var (context, scenario) = Stage(SoftCapDays);

        var result = new TokenRepair(new TokenConstraintChecker())
            .Apply(new TokenOperatorContext(scenario, null, context, new Random(0)));

        result.Tokens.ShouldContain(t => t.Date == FirstDay.AddDays(SoftCapDays) && t.AgentId == StandInId);
    }

    private static (CoreWizardContext Context, CoreScenario Scenario) Stage(int maxConsecutiveDays)
    {
        var shifts = Enumerable.Range(0, SoftCapDays + 1).Select(offset => Shift(FirstDay.AddDays(offset))).ToList();
        var lockedWorks = shifts
            .Take(SoftCapDays)
            .Select((shift, index) => LockedWork(FirstDay.AddDays(index), shift))
            .ToList();
        var context = new CoreWizardContext
        {
            PeriodFrom = FirstDay,
            PeriodUntil = FirstDay.AddDays(SoftCapDays),
            Agents = [Agent(RealId, maxConsecutiveDays), Agent(StandInId, maxConsecutiveDays) with { IsStandIn = true }],
            Shifts = shifts,
            LockedWorks = lockedWorks,
            SchedulingMaxConsecutiveDays = maxConsecutiveDays,
            SchedulingMaxDailyHours = 10,
            SchedulingMinPauseHours = 11,
        };
        var scenario = new CoreScenario
        {
            Id = "s",
            Tokens = lockedWorks.Select(work => new CoreToken(
                [work.WorkId], 0, work.Date, work.TotalHours, work.StartAt, work.EndAt,
                Guid.NewGuid(), 0, true, null, work.ShiftRefId, work.AgentId)).ToList(),
        };
        return (context, scenario);
    }

    private static CoreLockedWork LockedWork(DateOnly date, CoreShift shift) => new(
        WorkId: shift.Id,
        AgentId: RealId,
        Date: date,
        ShiftTypeIndex: 0,
        TotalHours: SlotHours,
        StartAt: date.ToDateTime(new TimeOnly(8, 0)),
        EndAt: date.ToDateTime(new TimeOnly(16, 0)),
        ShiftRefId: Guid.Parse(shift.Id),
        LocationContext: null);

    private static CoreShift Shift(DateOnly date) => new(
        Guid.NewGuid().ToString(),
        date.ToString(DateFormat, CultureInfo.InvariantCulture),
        date.ToString(DateFormat, CultureInfo.InvariantCulture),
        "08:00",
        "16:00",
        (double)SlotHours,
        1,
        0);

    private static CoreAgent Agent(string id, int maxConsecutiveDays) => new(
        Id: id,
        CurrentHours: 0,
        GuaranteedHours: 0,
        MaxConsecutiveDays: maxConsecutiveDays,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        MaxWorkDays = SoftCapDays,
        MinRestDays = 2,
        PerformsShiftWork = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
    };
}
