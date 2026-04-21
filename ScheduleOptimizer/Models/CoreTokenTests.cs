// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using FluentAssertions;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Models;

[TestFixture]
public class CoreTokenTests
{
    [Test]
    public void CoreToken_StoresAllRequiredFields()
    {
        var token = new CoreToken(
            WorkIds: ["work-1", "work-2"],
            ShiftTypeIndex: 0,
            Date: new DateOnly(2026, 4, 21),
            TotalHours: 8.5m,
            StartAt: new DateTime(2026, 4, 21, 8, 0, 0),
            EndAt: new DateTime(2026, 4, 21, 17, 0, 0),
            BlockId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            PositionInBlock: 2,
            IsLocked: false,
            LocationContext: "office-01",
            ShiftRefId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            AgentId: "agent-007");

        token.WorkIds.Should().HaveCount(2);
        token.ShiftTypeIndex.Should().Be(0);
        token.Date.Should().Be(new DateOnly(2026, 4, 21));
        token.TotalHours.Should().Be(8.5m);
        token.PositionInBlock.Should().Be(2);
        token.IsLocked.Should().BeFalse();
        token.LocationContext.Should().Be("office-01");
        token.AgentId.Should().Be("agent-007");
    }

    [Test]
    public void CoreToken_LocationContextNullable()
    {
        var token = new CoreToken(
            WorkIds: ["work-1"],
            ShiftTypeIndex: 1,
            Date: new DateOnly(2026, 4, 21),
            TotalHours: 8m,
            StartAt: new DateTime(2026, 4, 21, 14, 0, 0),
            EndAt: new DateTime(2026, 4, 21, 22, 0, 0),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: true,
            LocationContext: null,
            ShiftRefId: Guid.NewGuid(),
            AgentId: "agent-008");

        token.LocationContext.Should().BeNull();
        token.IsLocked.Should().BeTrue();
    }
}
