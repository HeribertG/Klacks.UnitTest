// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Constraints;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Operators;

[TestFixture]
public class GaOperatorTests
{
    private static CoreAgent MakeAgent(string id)
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

    private static CoreToken MakeToken(string agentId, DateOnly date, Guid? blockId = null, bool isLocked = false, int shiftTypeIndex = 0)
    {
        return new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: shiftTypeIndex,
            Date: date,
            TotalHours: 8,
            StartAt: date.ToDateTime(new TimeOnly(8, 0)),
            EndAt: date.ToDateTime(new TimeOnly(16, 0)),
            BlockId: blockId ?? Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: isLocked,
            LocationContext: null,
            ShiftRefId: Guid.NewGuid(),
            AgentId: agentId);
    }

    private static CoreWizardContext Ctx(params CoreAgent[] agents)
    {
        return new CoreWizardContext
        {
            PeriodFrom = new DateOnly(2026, 4, 20),
            PeriodUntil = new DateOnly(2026, 4, 30),
            Agents = agents,
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMinPauseHours = 11,
        };
    }

    [Test]
    public void TokenSwapMutation_SwapsAgentAssignmentOfTwoTokens()
    {
        var date = new DateOnly(2026, 4, 20);
        var scenario = new CoreScenario
        {
            Id = "s",
            Tokens =
            [
                MakeToken("A", date),
                MakeToken("B", date.AddDays(1)),
            ],
        };

        var sut = new TokenSwapMutation();
        var result = sut.Apply(new TokenOperatorContext(scenario, null, Ctx(MakeAgent("A"), MakeAgent("B")), new Random(0)));

        result.Tokens.Count().ShouldBe(2);
        var agentIds = result.Tokens.Select(t => t.AgentId).ToList();
        agentIds.ShouldContain("A");
        agentIds.ShouldContain("B");
    }

    [Test]
    public void TokenSwapMutation_DoesNotTouchLockedTokens()
    {
        var date = new DateOnly(2026, 4, 20);
        var scenario = new CoreScenario
        {
            Id = "s",
            Tokens = [MakeToken("A", date, isLocked: true)],
        };

        var sut = new TokenSwapMutation();
        var result = sut.Apply(new TokenOperatorContext(scenario, null, Ctx(MakeAgent("A")), new Random(0)));

        result.Tokens.Single().AgentId.ShouldBe("A");
        result.Tokens.Single().IsLocked.ShouldBeTrue();
    }

    [Test]
    public void BlockSplitMutation_SplitsBlockIntoTwoBlockIds()
    {
        var blockId = Guid.NewGuid();
        var start = new DateOnly(2026, 4, 20);
        var tokens = Enumerable.Range(0, 4)
            .Select(i => MakeToken("A", start.AddDays(i), blockId: blockId))
            .ToList();

        var scenario = new CoreScenario { Id = "s", Tokens = tokens };
        var sut = new BlockSplitMutation();
        var result = sut.Apply(new TokenOperatorContext(scenario, null, Ctx(MakeAgent("A")), new Random(0)));

        var distinctBlocks = result.Tokens.Select(t => t.BlockId).Distinct().Count();
        distinctBlocks.ShouldBe(2);
    }

    [Test]
    public void BlockMergeMutation_MergesTwoConsecutiveBlocks()
    {
        var blockA = Guid.NewGuid();
        var blockB = Guid.NewGuid();
        var start = new DateOnly(2026, 4, 20);

        var tokens = new List<CoreToken>
        {
            MakeToken("A", start, blockId: blockA),
            MakeToken("A", start.AddDays(1), blockId: blockB),
        };

        var scenario = new CoreScenario { Id = "s", Tokens = tokens };
        var sut = new BlockMergeMutation();
        var result = sut.Apply(new TokenOperatorContext(scenario, null, Ctx(MakeAgent("A")), new Random(0)));

        result.Tokens.Select(t => t.BlockId).Distinct().Count().ShouldBe(1);
    }

    [Test]
    public void ReassignMutation_MovesTokenToAnotherValidAgent()
    {
        var date = new DateOnly(2026, 4, 20);
        var scenario = new CoreScenario { Id = "s", Tokens = [MakeToken("A", date)] };

        var sut = new ReassignMutation();
        var result = sut.Apply(new TokenOperatorContext(scenario, null, Ctx(MakeAgent("A"), MakeAgent("B")), new Random(0)));

        result.Tokens.Single().AgentId.ShouldBe("B");
    }

    [Test]
    public void BlockCrossover_ProducesChildWithoutDuplicateSlotKeys()
    {
        var date = new DateOnly(2026, 4, 20);
        var sharedShift = Guid.NewGuid();

        var parentA = new CoreScenario
        {
            Id = "a",
            Tokens = [MakeToken("A", date) with { ShiftRefId = sharedShift }],
        };
        var parentB = new CoreScenario
        {
            Id = "b",
            Tokens = [MakeToken("A", date) with { ShiftRefId = sharedShift }],
        };

        var sut = new BlockCrossover();
        var result = sut.Apply(new TokenOperatorContext(parentA, parentB, Ctx(MakeAgent("A")), new Random(0)));

        result.Tokens.Count(t => t.AgentId == "A" && t.Date == date && t.ShiftRefId == sharedShift)
            .ShouldBe(1);
    }

    [Test]
    public void TokenRepair_RemovesAViolationCausingToken()
    {
        var date = new DateOnly(2026, 4, 20);
        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [MakeAgent("A")],
            ScheduleCommands = [new CoreScheduleCommand("A", date, ScheduleCommandKeyword.Free)],
        };

        var scenario = new CoreScenario
        {
            Id = "s",
            Tokens = [MakeToken("A", date)],
        };

        var sut = new TokenRepair(new TokenConstraintChecker());
        var result = sut.Apply(new TokenOperatorContext(scenario, null, context, new Random(0)));

        result.Tokens.ShouldBeEmpty();
    }

    [Test]
    public void TokenRepair_AddsTokenForUnderSuppliedSlot()
    {
        var date = new DateOnly(2026, 4, 20);
        var shiftId = Guid.NewGuid();

        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [MakeAgent("A")],
            Shifts =
            [
                new CoreShift(shiftId.ToString(), "Shift-A", date.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, 1, 0),
            ],
        };

        var scenario = new CoreScenario { Id = "s", Tokens = [] };

        var sut = new TokenRepair(new TokenConstraintChecker());
        var result = sut.Apply(new TokenOperatorContext(scenario, null, context, new Random(0)));

        result.Tokens.ShouldHaveSingleItem();
        result.Tokens[0].ShiftRefId.ShouldBe(shiftId);
        result.Tokens[0].Date.ShouldBe(date);
        result.Tokens[0].AgentId.ShouldBe("A");
    }

    [Test]
    public void TokenRepair_PrefersUnderSupplyOverTokenScopedViolations()
    {
        var date = new DateOnly(2026, 4, 20);
        var shiftId = Guid.NewGuid();
        var tokenOnFreeDay = MakeToken("A", date);

        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [MakeAgent("A"), MakeAgent("B")],
            Shifts =
            [
                new CoreShift(shiftId.ToString(), "Shift-B", date.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, 1, 0),
            ],
            ScheduleCommands = [new CoreScheduleCommand("A", date, ScheduleCommandKeyword.Free)],
        };

        var scenario = new CoreScenario { Id = "s", Tokens = [tokenOnFreeDay] };

        var sut = new TokenRepair(new TokenConstraintChecker());
        var result = sut.Apply(new TokenOperatorContext(scenario, null, context, new Random(0)));

        result.Tokens.Count().ShouldBe(2);
        result.Tokens.ShouldContain(t => t.ShiftRefId == shiftId && t.AgentId == "B");
    }
}
