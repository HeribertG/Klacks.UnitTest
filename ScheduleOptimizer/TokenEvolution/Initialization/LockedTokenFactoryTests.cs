// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using FluentAssertions;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

[TestFixture]
public class LockedTokenFactoryTests
{
    private static CoreLockedWork MakeLocked(string agentId, DateOnly date, string suffix = "a")
    {
        return new CoreLockedWork(
            WorkId: $"w-{agentId}-{date:yyyyMMdd}-{suffix}",
            AgentId: agentId,
            Date: date,
            ShiftTypeIndex: 0,
            TotalHours: 8m,
            StartAt: date.ToDateTime(new TimeOnly(8, 0)),
            EndAt: date.ToDateTime(new TimeOnly(16, 0)),
            ShiftRefId: Guid.NewGuid(),
            LocationContext: null);
    }

    [Test]
    public void BuildLockedTokens_TwoConsecutiveDaysSameAgent_ShareBlockIdAndPositions()
    {
        var locked = new[]
        {
            MakeLocked("agent-A", new DateOnly(2026, 4, 20)),
            MakeLocked("agent-A", new DateOnly(2026, 4, 21)),
        };

        var tokens = LockedTokenFactory.BuildLockedTokens(locked, maxConsecutiveDays: 6);

        tokens.Should().HaveCount(2);
        tokens[0].BlockId.Should().Be(tokens[1].BlockId);
        tokens[0].PositionInBlock.Should().Be(0);
        tokens[1].PositionInBlock.Should().Be(1);
        tokens.Should().OnlyContain(t => t.IsLocked);
    }

    [Test]
    public void BuildLockedTokens_NonConsecutiveDaysSameAgent_HaveDifferentBlockIds()
    {
        var locked = new[]
        {
            MakeLocked("agent-A", new DateOnly(2026, 4, 20)),
            MakeLocked("agent-A", new DateOnly(2026, 4, 22)),
        };

        var tokens = LockedTokenFactory.BuildLockedTokens(locked, maxConsecutiveDays: 6);

        tokens[0].BlockId.Should().NotBe(tokens[1].BlockId);
        tokens[0].PositionInBlock.Should().Be(0);
        tokens[1].PositionInBlock.Should().Be(0);
    }

    [Test]
    public void BuildLockedTokens_DifferentAgents_HaveDifferentBlockIds()
    {
        var locked = new[]
        {
            MakeLocked("agent-A", new DateOnly(2026, 4, 20)),
            MakeLocked("agent-B", new DateOnly(2026, 4, 20)),
        };

        var tokens = LockedTokenFactory.BuildLockedTokens(locked, maxConsecutiveDays: 6);

        var agentATokens = tokens.Where(t => t.AgentId == "agent-A").ToList();
        var agentBTokens = tokens.Where(t => t.AgentId == "agent-B").ToList();

        agentATokens[0].BlockId.Should().NotBe(agentBTokens[0].BlockId);
    }

    [Test]
    public void BuildLockedTokens_ExceedsMaxConsecutiveDays_StartsNewBlock()
    {
        var dates = Enumerable.Range(0, 8)
            .Select(i => new DateOnly(2026, 4, 20).AddDays(i))
            .ToList();
        var locked = dates.Select(d => MakeLocked("agent-A", d)).ToList();

        var tokens = LockedTokenFactory.BuildLockedTokens(locked, maxConsecutiveDays: 6);

        tokens.Should().HaveCount(8);
        var firstBlock = tokens[0].BlockId;
        var firstBlockCount = tokens.Count(t => t.BlockId == firstBlock);
        firstBlockCount.Should().BeLessThanOrEqualTo(6);
    }

    [Test]
    public void BuildLockedTokens_TwoWorksOnSameDaySameAgent_StayInSameBlockWithPositionIncrement()
    {
        var date = new DateOnly(2026, 4, 20);
        var locked = new[]
        {
            MakeLocked("agent-A", date, "morning"),
            MakeLocked("agent-A", date, "evening"),
        };

        var tokens = LockedTokenFactory.BuildLockedTokens(locked, maxConsecutiveDays: 6);

        tokens.Should().HaveCount(2);
        tokens[0].BlockId.Should().Be(tokens[1].BlockId);
        tokens[0].PositionInBlock.Should().Be(0);
        tokens[1].PositionInBlock.Should().Be(1);
    }
}
