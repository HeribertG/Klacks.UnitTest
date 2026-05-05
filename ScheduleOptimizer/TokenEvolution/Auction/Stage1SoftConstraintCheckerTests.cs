// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Controller;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Auction;

[TestFixture]
public class Stage1SoftConstraintCheckerTests
{
    private static CoreAgent MakeAgent(int maxWorkDays = 5, int minRestDays = 2)
    {
        return new CoreAgent(
            Id: "A",
            CurrentHours: 0,
            GuaranteedHours: 180,
            MaxConsecutiveDays: 6,
            MinRestHours: 11,
            Motivation: 0.5,
            MaxDailyHours: 10,
            MaxWeeklyHours: 50,
            MaxOptimalGap: 2)
        {
            FullTime = 180,
            MaxWorkDays = maxWorkDays,
            MinRestDays = minRestDays,
            PerformsShiftWork = true,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
            WorkOnSaturday = true,
            WorkOnSunday = true,
        };
    }

    private static CoreShift MakeShift(DateOnly date) =>
        new(Guid.NewGuid().ToString(), "FD", date.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, 1, 0);

    private static CoreToken MakeToken(string agentId, DateOnly date) => new(
        WorkIds: [],
        ShiftTypeIndex: 0,
        Date: date,
        TotalHours: 8m,
        StartAt: date.ToDateTime(new TimeOnly(8, 0)),
        EndAt: date.ToDateTime(new TimeOnly(16, 0)),
        BlockId: Guid.NewGuid(),
        PositionInBlock: 0,
        IsLocked: false,
        LocationContext: null,
        ShiftRefId: Guid.Empty,
        AgentId: agentId);

    private static CoreWizardContext EmptyContext() => new()
    {
        PeriodFrom = new DateOnly(2026, 4, 20),
        PeriodUntil = new DateOnly(2026, 4, 30),
        SchedulingMaxConsecutiveDays = 6,
    };

    [Test]
    public void Check_BlockExceedsMaxWorkDays_Vetoes()
    {
        var sut = new Stage1SoftConstraintChecker();
        var agent = MakeAgent(maxWorkDays: 5);
        var d0 = new DateOnly(2026, 4, 20);
        var assigned = new[]
        {
            MakeToken("A", d0),
            MakeToken("A", d0.AddDays(1)),
            MakeToken("A", d0.AddDays(2)),
            MakeToken("A", d0.AddDays(3)),
            MakeToken("A", d0.AddDays(4)),
        };
        var slot = MakeShift(d0.AddDays(5));

        var verdict = sut.Check(agent, slot, assigned, EmptyContext());

        verdict.ShouldNotBeNull();
        verdict!.Stage.ShouldBe(1);
        verdict.RuleName.ShouldBe("MaxWorkDays");
    }

    [Test]
    public void Check_GapToPreviousBlockBelowMinRestDays_Vetoes()
    {
        var sut = new Stage1SoftConstraintChecker();
        var agent = MakeAgent(minRestDays: 2);
        var endOfBlock = new DateOnly(2026, 4, 24);
        var assigned = new[] { MakeToken("A", endOfBlock) };
        var newSlot = MakeShift(endOfBlock.AddDays(2));

        var verdict = sut.Check(agent, newSlot, assigned, EmptyContext());

        verdict.ShouldNotBeNull();
        verdict!.RuleName.ShouldBe("MinRestDays");
    }

    [Test]
    public void Check_GapToPreviousBlockMeetsMinRestDays_NoVeto()
    {
        var sut = new Stage1SoftConstraintChecker();
        var agent = MakeAgent(minRestDays: 2);
        var endOfBlock = new DateOnly(2026, 4, 24);
        var assigned = new[] { MakeToken("A", endOfBlock) };
        var newSlot = MakeShift(endOfBlock.AddDays(3));

        var verdict = sut.Check(agent, newSlot, assigned, EmptyContext());

        verdict.ShouldBeNull();
    }

    private static CoreToken MakeNightToken(string agentId, DateOnly anchor) => new(
        WorkIds: [],
        ShiftTypeIndex: 2,
        Date: anchor,
        TotalHours: 8m,
        StartAt: anchor.ToDateTime(new TimeOnly(23, 0)),
        EndAt: anchor.AddDays(1).ToDateTime(new TimeOnly(7, 0)),
        BlockId: Guid.NewGuid(),
        PositionInBlock: 0,
        IsLocked: false,
        LocationContext: null,
        ShiftRefId: Guid.Empty,
        AgentId: agentId);

    [Test]
    public void Check_NightShiftSpilloverCountsAsOccupiedDay_Vetoes()
    {
        // Night shift Mon 23:00 -> Tue 07:00 occupies both Mon (anchor) and Tue (spillover).
        // With MinRestDays = 1, attempting to schedule a slot on Wed leaves only 0 actual
        // free days (Tue is half-occupied), so it must veto.
        var sut = new Stage1SoftConstraintChecker();
        var agent = MakeAgent(minRestDays: 1);
        var monday = new DateOnly(2026, 4, 20);
        var assigned = new[] { MakeNightToken("A", monday) };
        var wednesdaySlot = MakeShift(monday.AddDays(2));

        var verdict = sut.Check(agent, wednesdaySlot, assigned, EmptyContext());

        verdict.ShouldNotBeNull();
        verdict!.RuleName.ShouldBe("MinRestDays");
    }

    [Test]
    public void Check_NightShiftSpillover_GapMetWithExtraDay_NoVeto()
    {
        // Night shift Mon 23 -> Tue 07. With MinRestDays = 1, scheduling Thursday gives
        // exactly 1 free day (Wed) after the Tue spillover -> allowed.
        var sut = new Stage1SoftConstraintChecker();
        var agent = MakeAgent(minRestDays: 1);
        var monday = new DateOnly(2026, 4, 20);
        var assigned = new[] { MakeNightToken("A", monday) };
        var thursdaySlot = MakeShift(monday.AddDays(3));

        var verdict = sut.Check(agent, thursdaySlot, assigned, EmptyContext());

        verdict.ShouldBeNull();
    }

    [Test]
    public void Check_FifthDayInBlock_NoVeto()
    {
        var sut = new Stage1SoftConstraintChecker();
        var agent = MakeAgent(maxWorkDays: 5);
        var d0 = new DateOnly(2026, 4, 20);
        var assigned = new[]
        {
            MakeToken("A", d0),
            MakeToken("A", d0.AddDays(1)),
            MakeToken("A", d0.AddDays(2)),
            MakeToken("A", d0.AddDays(3)),
        };
        var slot = MakeShift(d0.AddDays(4));

        var verdict = sut.Check(agent, slot, assigned, EmptyContext());

        verdict.ShouldBeNull();
    }
}
