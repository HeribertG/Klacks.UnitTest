// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using FluentAssertions;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Models;

[TestFixture]
public class CoreAgentTests
{
    [Test]
    public void CoreAgent_WithExtendedFields_AllOptionalDefaultsApply()
    {
        var agent = new CoreAgent(
            Id: "agent-007",
            CurrentHours: 12,
            GuaranteedHours: 0,
            MaxConsecutiveDays: 6,
            MinRestHours: 11,
            Motivation: 0.5,
            MaxDailyHours: 10,
            MaxWeeklyHours: 50,
            MaxOptimalGap: 2);

        agent.FullTime.Should().Be(0);
        agent.MaximumHours.Should().Be(0);
        agent.MinimumHours.Should().Be(0);
        agent.PerformsShiftWork.Should().BeTrue();
        agent.WorkOnMonday.Should().BeTrue();
        agent.WorkOnSunday.Should().BeFalse();
    }

    [Test]
    public void CoreAgent_WithExtendedFields_AcceptsAllNewProperties()
    {
        var agent = new CoreAgent(
            Id: "agent-008",
            CurrentHours: 0,
            GuaranteedHours: 30,
            MaxConsecutiveDays: 6,
            MinRestHours: 11,
            Motivation: 0.5,
            MaxDailyHours: 10,
            MaxWeeklyHours: 50,
            MaxOptimalGap: 2)
        {
            FullTime = 40,
            MaximumHours = 45,
            MinimumHours = 25,
            PerformsShiftWork = false,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
            WorkOnSaturday = false,
            WorkOnSunday = false,
        };

        agent.FullTime.Should().Be(40);
        agent.PerformsShiftWork.Should().BeFalse();
    }
}
