// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
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

        scenario.Tokens.ShouldNotBeEmpty();
        scenario.Tokens.ShouldAllBe(t => t.AgentId == "FT");
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

        scenario.Tokens.Count().ShouldBe(1);
        scenario.Tokens[0].IsLocked.ShouldBeTrue();
    }

    [Test]
    public void BuildScenario_EnforcesFullSlotCoverage_EvenBeyondTarget()
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

        scenario.Tokens.Where(t => !t.IsLocked).Count().ShouldBe(3);
    }
    [Test]
    public void BuildScenario_ForcedCoverage_NeverDoubleBooksTheOnlyAgent()
    {
        // Two shifts overlap on the same day and only one agent exists. Forced coverage may leave the
        // second slot empty, but it must never put the same person on both: a plan with a gap is a plan,
        // a plan with one human in two places is not.
        var agent = MakeAgent("A", fullTime: 8);
        var date = new DateOnly(2026, 4, 20);

        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [agent],
            Shifts =
            [
                MakeShift(date, Guid.NewGuid().ToString()),
                new CoreShift(Guid.NewGuid().ToString(), "FD", date.ToString("yyyy-MM-dd"), "12:00", "20:00", 8, 1, 0),
            ],
            SchedulingMaxConsecutiveDays = 6,
        };

        var scenario = new GreedyTokenStrategy { Epsilon = 0 }.BuildScenario(context, new Random(0));

        var assigned = scenario.Tokens.Where(t => t.AgentId == "A").OrderBy(t => t.StartAt).ToList();
        for (var i = 1; i < assigned.Count; i++)
        {
            assigned[i].StartAt.ShouldBeGreaterThanOrEqualTo(assigned[i - 1].EndAt);
        }
    }

    [Test]
    public void BuildScenario_ForcedCoverage_NeverCollidesWithAnExistingWork()
    {
        var agent = MakeAgent("A", fullTime: 8);
        var date = new DateOnly(2026, 4, 20);

        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [agent],
            Shifts = [MakeShift(date, Guid.NewGuid().ToString())],
            ExistingWorkBlockers =
            [
                new CoreExistingWorkBlocker("A", date, date.ToDateTime(new TimeOnly(7, 0)), date.ToDateTime(new TimeOnly(15, 0))),
            ],
            SchedulingMaxConsecutiveDays = 6,
        };

        var scenario = new GreedyTokenStrategy { Epsilon = 0 }.BuildScenario(context, new Random(0));

        scenario.Tokens.ShouldNotContain(t => t.AgentId == "A" && !t.IsLocked);
    }
}
