// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using FluentAssertions;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Controller;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Auction;

[TestFixture]
public class Stage0HardConstraintCheckerTests
{
    private static CoreAgent MakeAgent(
        string id = "A",
        bool sat = false,
        bool sun = false,
        bool performsShiftWork = true,
        double maximumHours = 0,
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
            FullTime = 40,
            MaximumHours = maximumHours,
            MaxWorkDays = 5,
            MinRestDays = 2,
            PerformsShiftWork = performsShiftWork,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
            WorkOnSaturday = sat,
            WorkOnSunday = sun,
        };
    }

    private static CoreShift MakeShift(DateOnly date, string start = "08:00", string end = "16:00", double hours = 8) =>
        new(Guid.NewGuid().ToString(), "FD", date.ToString("yyyy-MM-dd"), start, end, hours, 1, 0);

    private static CoreWizardContext EmptyContext() => new()
    {
        PeriodFrom = new DateOnly(2026, 4, 20),
        PeriodUntil = new DateOnly(2026, 4, 26),
        SchedulingMaxConsecutiveDays = 6,
        SchedulingMaxDailyHours = 10,
    };

    [Test]
    public void Check_ContractWeekday_VetoesUnworkableDay()
    {
        var sut = new Stage0HardConstraintChecker();
        var agent = MakeAgent(sat: false);
        var saturday = new DateOnly(2026, 4, 25);
        var slot = MakeShift(saturday);

        var verdict = sut.Check(agent, slot, [], EmptyContext());

        verdict.Should().NotBeNull();
        verdict!.Stage.Should().Be(0);
        verdict.RuleName.Should().Be("ContractWeekday");
    }

    [Test]
    public void Check_FreeKeyword_VetoesSlot()
    {
        var sut = new Stage0HardConstraintChecker();
        var agent = MakeAgent();
        var date = new DateOnly(2026, 4, 21);
        var slot = MakeShift(date);
        var ctx = new CoreWizardContext
        {
            PeriodFrom = new DateOnly(2026, 4, 20),
            PeriodUntil = new DateOnly(2026, 4, 26),
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMaxDailyHours = 10,
            ScheduleCommands = new List<CoreScheduleCommand>
            {
                new("A", date, ScheduleCommandKeyword.Free),
            },
        };

        var verdict = sut.Check(agent, slot, [], ctx);

        verdict.Should().NotBeNull();
        verdict!.RuleName.Should().Be("KeywordFree");
    }

    [Test]
    public void Check_OverlappingShiftSameDay_Vetoes()
    {
        var sut = new Stage0HardConstraintChecker();
        var agent = MakeAgent();
        var date = new DateOnly(2026, 4, 21);
        var existing = new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: 0,
            Date: date,
            TotalHours: 1m,
            StartAt: date.ToDateTime(new TimeOnly(7, 0)),
            EndAt: date.ToDateTime(new TimeOnly(8, 0)),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: null,
            ShiftRefId: Guid.Empty,
            AgentId: "A");
        var newSlot = MakeShift(date, "07:30", "08:30", hours: 1);

        var verdict = sut.Check(agent, newSlot, [existing], EmptyContext());

        verdict.Should().NotBeNull();
        verdict!.RuleName.Should().Be("OverlappingShift");
    }

    [Test]
    public void Check_NoViolation_ReturnsNull()
    {
        var sut = new Stage0HardConstraintChecker();
        var agent = MakeAgent();
        var date = new DateOnly(2026, 4, 21);
        var slot = MakeShift(date);

        var verdict = sut.Check(agent, slot, [], EmptyContext());

        verdict.Should().BeNull();
    }

    [Test]
    public void Check_MaxDailyHoursExceeded_Vetoes()
    {
        var sut = new Stage0HardConstraintChecker();
        var agent = MakeAgent();
        var date = new DateOnly(2026, 4, 21);
        var existing = new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: 0,
            Date: date,
            TotalHours: 7m,
            StartAt: date.ToDateTime(new TimeOnly(0, 0)),
            EndAt: date.ToDateTime(new TimeOnly(7, 0)),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: null,
            ShiftRefId: Guid.Empty,
            AgentId: "A");
        var slot = MakeShift(date, "08:00", "16:00", hours: 8);

        var verdict = sut.Check(agent, slot, [existing], EmptyContext());

        verdict.Should().NotBeNull();
        verdict!.RuleName.Should().Be("MaxDailyHours");
    }
}
