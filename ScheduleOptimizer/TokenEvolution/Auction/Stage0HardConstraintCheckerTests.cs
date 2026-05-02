// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
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

        verdict.ShouldNotBeNull();
        verdict!.Stage.ShouldBe(0);
        verdict.RuleName.ShouldBe("ContractWeekday");
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

        verdict.ShouldNotBeNull();
        verdict!.RuleName.ShouldBe("KeywordFree");
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

        verdict.ShouldNotBeNull();
        verdict!.RuleName.ShouldBe("OverlappingShift");
    }

    [Test]
    public void Check_NoViolation_ReturnsNull()
    {
        var sut = new Stage0HardConstraintChecker();
        var agent = MakeAgent();
        var date = new DateOnly(2026, 4, 21);
        var slot = MakeShift(date);

        var verdict = sut.Check(agent, slot, [], EmptyContext());

        verdict.ShouldBeNull();
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

        verdict.ShouldNotBeNull();
        verdict!.RuleName.ShouldBe("MaxDailyHours");
    }

    [Test]
    public void Check_MinPauseHours_VetoesBackToBackOvernightAndDayShift()
    {
        var sut = new Stage0HardConstraintChecker();
        var agent = MakeAgent();
        var nightDate = new DateOnly(2026, 4, 16);
        var dayDate = new DateOnly(2026, 4, 17);

        // Existing overnight token: 22:00 day-1 -> 07:00 day-2
        var overnight = new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: 2,
            Date: nightDate,
            TotalHours: 9m,
            StartAt: nightDate.ToDateTime(new TimeOnly(22, 0)),
            EndAt: dayDate.ToDateTime(new TimeOnly(7, 0)),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: null,
            ShiftRefId: Guid.Empty,
            AgentId: "A");

        // Day shift starting exactly when overnight ends -> 0h rest
        var daySlot = MakeShift(dayDate, "07:00", "15:00", hours: 8);

        var verdict = sut.Check(agent, daySlot, [overnight], EmptyContext());

        verdict.ShouldNotBeNull();
        verdict!.RuleName.ShouldBe("MinPauseHours");
    }

    [Test]
    public void Check_OverlappingShiftCrossDay_VetoesOvernightTailIntoNextDay()
    {
        var sut = new Stage0HardConstraintChecker();
        var agent = MakeAgent();
        var nightDate = new DateOnly(2026, 4, 16);
        var dayDate = new DateOnly(2026, 4, 17);

        // Existing overnight token spans into 09:00 next day
        var overnight = new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: 2,
            Date: nightDate,
            TotalHours: 11m,
            StartAt: nightDate.ToDateTime(new TimeOnly(22, 0)),
            EndAt: dayDate.ToDateTime(new TimeOnly(9, 0)),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: null,
            ShiftRefId: Guid.Empty,
            AgentId: "A");

        // New slot on the next day starts during the overnight tail (08:00-16:00)
        var daySlot = MakeShift(dayDate, "08:00", "16:00", hours: 8);

        var verdict = sut.Check(agent, daySlot, [overnight], EmptyContext());

        verdict.ShouldNotBeNull();
        verdict!.RuleName.ShouldBe("OverlappingShift");
    }

    [Test]
    public void Check_PerAgentMaxDailyHours_OverridesGlobalCap()
    {
        var sut = new Stage0HardConstraintChecker();
        // Agent contracted at 8h/day even though global default is 10h
        var agent = MakeAgent() with { MaxDailyHours = 8 };
        var date = new DateOnly(2026, 4, 21);
        var existing = new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: 0,
            Date: date,
            TotalHours: 5m,
            StartAt: date.ToDateTime(new TimeOnly(0, 0)),
            EndAt: date.ToDateTime(new TimeOnly(5, 0)),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: null,
            ShiftRefId: Guid.Empty,
            AgentId: "A");
        // 5h existing + 4h new = 9h. Global 10h would allow it; per-agent 8h vetoes.
        var slot = MakeShift(date, "08:00", "12:00", hours: 4);

        var verdict = sut.Check(agent, slot, [existing], EmptyContext());

        verdict.ShouldNotBeNull();
        verdict!.RuleName.ShouldBe("MaxDailyHours");
    }

    [Test]
    public void Check_MaxConsecutiveDays_VetoesNinthDayInARowWhenCapIsSix()
    {
        var sut = new Stage0HardConstraintChecker();
        // 7-day-a-week agent so weekday rules cannot pre-empt the consecutive-days check.
        var agent = MakeAgent(sat: true, sun: true);
        // Existing tokens for 8 consecutive days -> placing a 9th must be vetoed (cap = 6)
        var assigned = new List<CoreToken>();
        var blockStart = new DateOnly(2026, 4, 13); // Monday — block ends 2026-04-20 (Mon), 9th day = 2026-04-21 (Tue)
        for (var offset = 0; offset < 8; offset++)
        {
            var d = blockStart.AddDays(offset);
            assigned.Add(new CoreToken(
                WorkIds: [],
                ShiftTypeIndex: 0,
                Date: d,
                TotalHours: 8m,
                StartAt: d.ToDateTime(new TimeOnly(8, 0)),
                EndAt: d.ToDateTime(new TimeOnly(16, 0)),
                BlockId: Guid.NewGuid(),
                PositionInBlock: offset,
                IsLocked: false,
                LocationContext: null,
                ShiftRefId: Guid.Empty,
                AgentId: "A"));
        }

        var ninthDay = blockStart.AddDays(8);
        var slot = MakeShift(ninthDay, "20:00", "04:00", hours: 8); // late shift, no overlap

        var verdict = sut.Check(agent, slot, assigned, EmptyContext());

        verdict.ShouldNotBeNull();
        verdict!.RuleName.ShouldBe("MaxConsecutiveDays");
    }

    [Test]
    public void Check_MinPauseHours_VetoesAgainstLockedWork()
    {
        var sut = new Stage0HardConstraintChecker();
        var agent = MakeAgent();
        var nightDate = new DateOnly(2026, 4, 16);
        var dayDate = new DateOnly(2026, 4, 17);
        var ctx = new CoreWizardContext
        {
            PeriodFrom = new DateOnly(2026, 4, 13),
            PeriodUntil = new DateOnly(2026, 4, 26),
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMaxDailyHours = 10,
            SchedulingMinPauseHours = 11,
            LockedWorks = new List<CoreLockedWork>
            {
                new(
                    WorkId: "W1",
                    AgentId: "A",
                    Date: nightDate,
                    ShiftTypeIndex: 2,
                    TotalHours: 9m,
                    StartAt: nightDate.ToDateTime(new TimeOnly(22, 0)),
                    EndAt: dayDate.ToDateTime(new TimeOnly(7, 0)),
                    ShiftRefId: Guid.NewGuid(),
                    LocationContext: null),
            },
        };

        var daySlot = MakeShift(dayDate, "07:00", "15:00", hours: 8);

        var verdict = sut.Check(agent, daySlot, [], ctx);

        verdict.ShouldNotBeNull();
        verdict!.RuleName.ShouldBe("MinPauseHours");
    }
}
