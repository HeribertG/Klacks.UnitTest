// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using FluentAssertions;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

[TestFixture]
public class CoverageFirstTokenStrategyTests
{
    private static CoreAgent MakeAgent(string id, bool shiftWork = true)
    {
        return new CoreAgent(
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
            PerformsShiftWork = shiftWork,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
            WorkOnSaturday = true,
            WorkOnSunday = true,
        };
    }

    private static CoreShift MakeShift(Guid id, DateOnly date, string startTime = "08:00", string endTime = "16:00", double hours = 8)
    {
        return new CoreShift(id.ToString(), "FD", date.ToString("yyyy-MM-dd"), startTime, endTime, hours, 1, 0);
    }

    [Test]
    public void BuildScenario_AssignsOneAgentPerSlot_AchievingFullCoverage()
    {
        var date = new DateOnly(2026, 4, 20);
        var shifts = Enumerable.Range(0, 3)
            .Select(_ => MakeShift(Guid.NewGuid(), date))
            .ToList();
        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [MakeAgent("A"), MakeAgent("B"), MakeAgent("C")],
            Shifts = shifts,
        };

        var scenario = new CoverageFirstTokenStrategy().BuildScenario(context, new Random(0));

        scenario.Tokens.Should().HaveCount(3);
        scenario.Tokens.Select(t => t.ShiftRefId).Distinct().Should().HaveCount(3);
        scenario.Tokens.Select(t => t.AgentId).Distinct().Should().HaveCount(3);
    }

    [Test]
    public void BuildScenario_DoesNotDoubleBookAgentOnSameDay()
    {
        var date = new DateOnly(2026, 4, 20);
        var shifts = Enumerable.Range(0, 2)
            .Select(_ => MakeShift(Guid.NewGuid(), date))
            .ToList();
        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [MakeAgent("A"), MakeAgent("B")],
            Shifts = shifts,
            SchedulingMaxDailyHours = 8,
        };

        var scenario = new CoverageFirstTokenStrategy().BuildScenario(context, new Random(0));

        scenario.Tokens.GroupBy(t => (t.AgentId, t.Date))
            .Should().OnlyContain(g => g.Count() == 1);
    }

    [Test]
    public void BuildScenario_LeavesSlotEmpty_WhenNoValidAgentExists()
    {
        var date = new DateOnly(2026, 4, 20);
        var lateShift = MakeShift(Guid.NewGuid(), date, startTime: "15:00", endTime: "23:00");
        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [MakeAgent("A", shiftWork: false)],
            Shifts = [lateShift],
        };

        var scenario = new CoverageFirstTokenStrategy().BuildScenario(context, new Random(0));

        scenario.Tokens.Should().BeEmpty();
    }

    [Test]
    public void BuildScenario_FillsAllSlotsAcrossMultipleDays()
    {
        var startDate = new DateOnly(2026, 4, 20);
        var shifts = new List<CoreShift>();
        for (var day = 0; day < 3; day++)
        {
            shifts.Add(MakeShift(Guid.NewGuid(), startDate.AddDays(day)));
        }

        var context = new CoreWizardContext
        {
            PeriodFrom = startDate,
            PeriodUntil = startDate.AddDays(2),
            Agents = [MakeAgent("A")],
            Shifts = shifts,
            SchedulingMaxDailyHours = 10,
        };

        var scenario = new CoverageFirstTokenStrategy().BuildScenario(context, new Random(0));

        scenario.Tokens.Should().HaveCount(3);
        scenario.Tokens.Select(t => t.Date).Distinct().Should().HaveCount(3);
    }
}
