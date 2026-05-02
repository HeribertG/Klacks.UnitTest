// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Fitness;

[TestFixture]
public class MaxPossibleCalculatorTests
{
    private static CoreAgent MakeAgent(
        string id,
        bool shiftWork = true,
        bool workOnMon = true,
        bool workOnSun = false,
        double maxHours = 0,
        double currentHours = 0)
    {
        return new CoreAgent(
            Id: id,
            CurrentHours: currentHours,
            GuaranteedHours: 0,
            MaxConsecutiveDays: 6,
            MinRestHours: 11,
            Motivation: 0.5,
            MaxDailyHours: 10,
            MaxWeeklyHours: 50,
            MaxOptimalGap: 2)
        {
            MaximumHours = maxHours,
            PerformsShiftWork = shiftWork,
            WorkOnMonday = workOnMon,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
            WorkOnSunday = workOnSun,
        };
    }

    private static CoreShift MakeShift(DateOnly date, string start = "08:00", string end = "16:00", double hours = 8)
    {
        return new CoreShift(Guid.NewGuid().ToString(), "FD", date.ToString("yyyy-MM-dd"), start, end, hours, 1, 0);
    }

    [Test]
    public void ComputeForAll_RespectsWeekdayFlags()
    {
        var monday = new DateOnly(2026, 4, 20);
        var tuesday = monday.AddDays(1);
        var agent = MakeAgent("A", workOnMon: false);

        var context = new CoreWizardContext
        {
            PeriodFrom = monday,
            PeriodUntil = tuesday,
            Agents = [agent],
            Shifts = [MakeShift(monday), MakeShift(tuesday)],
        };

        var result = new MaxPossibleCalculator().ComputeForAll(context);

        result["A"].ShouldBe(8);
    }

    [Test]
    public void ComputeForAll_NonShiftAgent_SkipsLateAndNightShifts()
    {
        var date = new DateOnly(2026, 4, 20);
        var agent = MakeAgent("A", shiftWork: false);

        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [agent],
            Shifts = [MakeShift(date, start: "06:00"), MakeShift(date, start: "14:00"), MakeShift(date, start: "22:00")],
        };

        var result = new MaxPossibleCalculator().ComputeForAll(context);

        result["A"].ShouldBe(8);
    }

    [Test]
    public void ComputeForAll_AgentWithMaxHours_CapsTotal()
    {
        var date = new DateOnly(2026, 4, 20);
        var agent = MakeAgent("A", maxHours: 16, currentHours: 4);

        var shifts = Enumerable.Range(0, 5)
            .Select(i => MakeShift(date.AddDays(i)))
            .ToList();

        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date.AddDays(4),
            Agents = [agent],
            Shifts = shifts,
        };

        var result = new MaxPossibleCalculator().ComputeForAll(context);

        result["A"].ShouldBe(12);
    }

    [Test]
    public void ComputeForAll_BreakBlocker_ReducesAvailableHours()
    {
        var start = new DateOnly(2026, 4, 20);
        var agent = MakeAgent("A");

        var context = new CoreWizardContext
        {
            PeriodFrom = start,
            PeriodUntil = start.AddDays(2),
            Agents = [agent],
            Shifts = [MakeShift(start), MakeShift(start.AddDays(1)), MakeShift(start.AddDays(2))],
            BreakBlockers = [new CoreBreakBlocker("A", start.AddDays(1), start.AddDays(1), "Vac")],
        };

        var result = new MaxPossibleCalculator().ComputeForAll(context);

        result["A"].ShouldBe(16);
    }
}
