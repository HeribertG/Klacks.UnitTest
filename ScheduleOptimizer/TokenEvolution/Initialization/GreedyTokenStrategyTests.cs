// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using FluentAssertions;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

[TestFixture]
public class GreedyTokenStrategyTests
{
    private static CoreAgent MakeAgent(string id, double fullTime, double currentHours = 0)
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
            FullTime = fullTime,
            PerformsShiftWork = true,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
        };
    }

    private static CoreShift MakeShift(DateOnly date, string id)
    {
        return new CoreShift(id, "FD", date.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, 1, 0);
    }

    [Test]
    public void BuildScenario_FullTimeAgent_GetsHoursBeforePartTimeAgent()
    {
        var fullTime = MakeAgent("FT", fullTime: 40);
        var partTime = MakeAgent("PT", fullTime: 20);

        var date1 = new DateOnly(2026, 4, 20);
        var date2 = new DateOnly(2026, 4, 21);

        var shifts = new[]
        {
            MakeShift(date1, Guid.NewGuid().ToString()),
            MakeShift(date2, Guid.NewGuid().ToString()),
        };

        var context = new CoreWizardContext
        {
            PeriodFrom = date1,
            PeriodUntil = date2,
            Agents = [fullTime, partTime],
            Shifts = shifts,
            SchedulingMaxConsecutiveDays = 6,
        };

        var scenario = new GreedyTokenStrategy { Epsilon = 0 }.BuildScenario(context, new Random(0));

        scenario.Tokens.Should().NotBeEmpty();
        scenario.Tokens.Should().OnlyContain(t => t.AgentId == "FT");
    }

    [Test]
    public void BuildScenario_PreservesLockedTokens()
    {
        var agent = MakeAgent("A", fullTime: 40);
        var date = new DateOnly(2026, 4, 20);

        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [agent],
            Shifts = [],
            LockedWorks =
            [
                new CoreLockedWork(
                    WorkId: "w1",
                    AgentId: "A",
                    Date: date,
                    ShiftTypeIndex: 0,
                    TotalHours: 8m,
                    StartAt: date.ToDateTime(new TimeOnly(8, 0)),
                    EndAt: date.ToDateTime(new TimeOnly(16, 0)),
                    ShiftRefId: Guid.NewGuid(),
                    LocationContext: null),
            ],
            SchedulingMaxConsecutiveDays = 6,
        };

        var scenario = new GreedyTokenStrategy().BuildScenario(context, new Random(0));

        scenario.Tokens.Should().HaveCount(1);
        scenario.Tokens[0].IsLocked.Should().BeTrue();
    }

    [Test]
    public void BuildScenario_DoesNotExceedFullTime()
    {
        var agent = MakeAgent("A", fullTime: 16);
        var date1 = new DateOnly(2026, 4, 20);
        var date2 = new DateOnly(2026, 4, 21);
        var date3 = new DateOnly(2026, 4, 22);

        var shifts = new[]
        {
            MakeShift(date1, Guid.NewGuid().ToString()),
            MakeShift(date2, Guid.NewGuid().ToString()),
            MakeShift(date3, Guid.NewGuid().ToString()),
        };

        var context = new CoreWizardContext
        {
            PeriodFrom = date1,
            PeriodUntil = date3,
            Agents = [agent],
            Shifts = shifts,
            SchedulingMaxConsecutiveDays = 6,
        };

        var scenario = new GreedyTokenStrategy { Epsilon = 0 }.BuildScenario(context, new Random(0));

        scenario.Tokens.Where(t => !t.IsLocked).Should().HaveCountLessThanOrEqualTo(2);
    }
}
