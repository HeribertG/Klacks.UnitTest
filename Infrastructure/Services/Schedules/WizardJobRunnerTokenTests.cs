// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the WizardJobRunner.MapTokens static method, verifying filtering of locked tokens
/// and correct projection to WizardTokenDto.
/// </summary>

using Shouldly;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class WizardJobRunnerTokenTests
{
    [Test]
    public void MapTokens_ExcludesLockedTokens()
    {
        var shiftId = Guid.NewGuid();
        var agentToken = new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: 0,
            Date: new DateOnly(2026, 4, 22),
            TotalHours: 8m,
            StartAt: new DateTime(2026, 4, 22, 6, 0, 0),
            EndAt: new DateTime(2026, 4, 22, 14, 0, 0),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: null,
            ShiftRefId: shiftId,
            AgentId: "agent-1");

        var lockedToken = new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: 0,
            Date: new DateOnly(2026, 4, 22),
            TotalHours: 8m,
            StartAt: new DateTime(2026, 4, 22, 6, 0, 0),
            EndAt: new DateTime(2026, 4, 22, 14, 0, 0),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: true,
            LocationContext: null,
            ShiftRefId: Guid.NewGuid(),
            AgentId: "agent-2");

        var tokens = new List<CoreToken> { agentToken, lockedToken };

        var result = WizardJobRunner.MapTokens(tokens);

        result.Count().ShouldBe(1);
        result[0].AgentId.ShouldBe("agent-1");
        result[0].ShiftId.ShouldBe(shiftId.ToString());
        result[0].Date.ShouldBe("2026-04-22");
        result[0].StartTime.ShouldBe("06:00");
        result[0].EndTime.ShouldBe("14:00");
        result[0].Hours.ShouldBe(8m);
    }

    [Test]
    public void MapTokens_EmptyInput_ReturnsEmptyList()
    {
        var result = WizardJobRunner.MapTokens([]);

        result.ShouldBeEmpty();
    }
}
