// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using FluentAssertions;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Models;

[TestFixture]
public class CoreBlockTests
{
    [Test]
    public void CoreBlock_StoresAgentAndDateRange()
    {
        var blockId = Guid.NewGuid();
        var block = new CoreBlock(
            Id: blockId,
            AgentId: "agent-007",
            FirstDate: new DateOnly(2026, 4, 21),
            LastDate: new DateOnly(2026, 4, 25),
            DayCount: 5);

        block.Id.Should().Be(blockId);
        block.AgentId.Should().Be("agent-007");
        block.FirstDate.Should().Be(new DateOnly(2026, 4, 21));
        block.LastDate.Should().Be(new DateOnly(2026, 4, 25));
        block.DayCount.Should().Be(5);
    }
}
