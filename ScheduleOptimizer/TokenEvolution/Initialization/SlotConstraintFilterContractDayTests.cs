// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

[TestFixture]
public sealed class SlotConstraintFilterContractDayTests
{
    private static CoreAgent MakeAgent(string id = "A", bool sat = false) => new(
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
        WorkOnSaturday = sat,
    };

    private static CoreWizardContext MakeContext(params CoreContractDay[] contractDays) => new()
    {
        PeriodFrom = new DateOnly(2026, 6, 1),
        PeriodUntil = new DateOnly(2026, 6, 30),
        SchedulingMaxConsecutiveDays = 6,
        SchedulingMaxDailyHours = 10,
        ContractDays = contractDays,
    };

    [Test]
    public void IsValidAssignment_ContractDayNotActive_BlocksWorkableWeekday()
    {
        var agent = MakeAgent();
        var monday = new DateOnly(2026, 6, 1);
        var ctx = MakeContext(
            new CoreContractDay(agent.Id, monday, WorksOnDay: false, PerformsShiftWork: true, FullTimeShare: 1, MaximumHoursPerDay: 10, ContractId: Guid.Empty));

        var valid = SlotConstraintFilter.IsValidAssignment(agent, monday, 0, 8, ctx, []);

        valid.ShouldBeFalse();
    }

    [Test]
    public void IsValidAssignment_ContractDayActive_OverridesStaticWeekdayFlag()
    {
        var agent = MakeAgent(sat: false);
        var saturday = new DateOnly(2026, 6, 6);
        var ctx = MakeContext(
            new CoreContractDay(agent.Id, saturday, WorksOnDay: true, PerformsShiftWork: true, FullTimeShare: 1, MaximumHoursPerDay: 10, ContractId: Guid.Empty));

        var valid = SlotConstraintFilter.IsValidAssignment(agent, saturday, 0, 8, ctx, []);

        valid.ShouldBeTrue();
    }

    [Test]
    public void IsValidAssignment_NoContractDayInfo_FallsBackToStaticFlags()
    {
        var agent = MakeAgent(sat: false);
        var saturday = new DateOnly(2026, 6, 6);
        var monday = new DateOnly(2026, 6, 1);
        var ctx = MakeContext();

        SlotConstraintFilter.IsValidAssignment(agent, saturday, 0, 8, ctx, []).ShouldBeFalse();
        SlotConstraintFilter.IsValidAssignment(agent, monday, 0, 8, ctx, []).ShouldBeTrue();
    }
}
