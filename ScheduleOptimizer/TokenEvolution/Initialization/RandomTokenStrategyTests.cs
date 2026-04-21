// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using FluentAssertions;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

[TestFixture]
public class RandomTokenStrategyTests
{
    private static CoreAgent MakeAgent(string id, bool shiftWork = true, bool workOnMon = true, bool workOnSun = false, double maxHours = 0)
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
            WorkOnMonday = workOnMon,
            WorkOnSunday = workOnSun,
            MaximumHours = maxHours,
        };
    }

    private static CoreShift MakeShift(string id, DateOnly date, string startTime = "08:00", string endTime = "16:00", double hours = 8)
    {
        return new CoreShift(id, "FD", date.ToString("yyyy-MM-dd"), startTime, endTime, hours, 1, 0);
    }

    [Test]
    public void BuildScenario_PreservesLockedTokens()
    {
        var agent = MakeAgent("A");
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

        var scenario = new RandomTokenStrategy().BuildScenario(context, new Random(42));

        scenario.Tokens.Should().HaveCount(1);
        scenario.Tokens[0].IsLocked.Should().BeTrue();
    }

    [Test]
    public void BuildScenario_AssignsTokenForValidSlot()
    {
        var agent = MakeAgent("A");
        var date = new DateOnly(2026, 4, 20);

        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [agent],
            Shifts = [MakeShift(Guid.NewGuid().ToString(), date)],
            SchedulingMaxConsecutiveDays = 6,
        };

        var scenario = new RandomTokenStrategy().BuildScenario(context, new Random(42));

        scenario.Tokens.Should().HaveCount(1);
        scenario.Tokens[0].AgentId.Should().Be("A");
        scenario.Tokens[0].IsLocked.Should().BeFalse();
    }

    [Test]
    public void BuildScenario_LeavesSlotEmpty_WhenAgentCannotWorkOnDay()
    {
        var agent = MakeAgent("A", workOnMon: false);
        var monday = new DateOnly(2026, 4, 20);

        var context = new CoreWizardContext
        {
            PeriodFrom = monday,
            PeriodUntil = monday,
            Agents = [agent],
            Shifts = [MakeShift(Guid.NewGuid().ToString(), monday)],
            SchedulingMaxConsecutiveDays = 6,
        };

        var scenario = new RandomTokenStrategy().BuildScenario(context, new Random(42));

        scenario.Tokens.Should().BeEmpty();
    }

    [Test]
    public void BuildScenario_LeavesSlotEmpty_WhenBreakBlockerCoversDate()
    {
        var agent = MakeAgent("A");
        var date = new DateOnly(2026, 4, 20);

        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [agent],
            Shifts = [MakeShift(Guid.NewGuid().ToString(), date)],
            BreakBlockers = [new CoreBreakBlocker("A", date, date, "Vacation")],
            SchedulingMaxConsecutiveDays = 6,
        };

        var scenario = new RandomTokenStrategy().BuildScenario(context, new Random(42));

        scenario.Tokens.Should().BeEmpty();
    }

    [Test]
    public void BuildScenario_RespectsFreeKeyword()
    {
        var agent = MakeAgent("A");
        var date = new DateOnly(2026, 4, 20);

        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [agent],
            Shifts = [MakeShift(Guid.NewGuid().ToString(), date)],
            ScheduleCommands = [new CoreScheduleCommand("A", date, ScheduleCommandKeyword.Free)],
            SchedulingMaxConsecutiveDays = 6,
        };

        var scenario = new RandomTokenStrategy().BuildScenario(context, new Random(42));

        scenario.Tokens.Should().BeEmpty();
    }

    [Test]
    public void BuildScenario_AgentWithoutShiftWork_RejectsLateShift()
    {
        var agent = MakeAgent("A", shiftWork: false);
        var date = new DateOnly(2026, 4, 20);

        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [agent],
            Shifts = [MakeShift(Guid.NewGuid().ToString(), date, startTime: "14:00", endTime: "22:00")],
            SchedulingMaxConsecutiveDays = 6,
        };

        var scenario = new RandomTokenStrategy().BuildScenario(context, new Random(42));

        scenario.Tokens.Should().BeEmpty();
    }

    [Test]
    public void ShiftTypeInference_FromStartTime_ClassifiesCorrectly()
    {
        ShiftTypeInference.FromStartTime(new TimeOnly(6, 0)).Should().Be(0);
        ShiftTypeInference.FromStartTime(new TimeOnly(14, 0)).Should().Be(1);
        ShiftTypeInference.FromStartTime(new TimeOnly(22, 0)).Should().Be(2);
        ShiftTypeInference.FromStartTime(new TimeOnly(2, 0)).Should().Be(2);
    }
}
