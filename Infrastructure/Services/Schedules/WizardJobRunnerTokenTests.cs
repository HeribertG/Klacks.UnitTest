// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the WizardJobRunner.MapTokens static method, verifying filtering of locked tokens
/// and correct projection to WizardTokenDto.
/// </summary>

using Shouldly;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Diagnostics;
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

    [Test]
    public void MapUnfilledSlots_ProjectsTheDiagnosisOfAnUnsolvableSlot()
    {
        var shiftId = Guid.NewGuid();
        var day = new DateOnly(2026, 4, 22);
        var context = new CoreWizardContext
        {
            PeriodFrom = day,
            PeriodUntil = day,
            Agents = [VacationAgent("agent-1"), VacationAgent("agent-2")],
            Shifts = [new CoreShift(shiftId.ToString(), "FD", "2026-04-22", "06:00", "14:00", 8, 1, 0)],
            BreakBlockers =
            [
                new CoreBreakBlocker("agent-1", day, day, "Vacation", 8m),
                new CoreBreakBlocker("agent-2", day, day, "Vacation", 8m),
            ],
            SchedulingMaxConsecutiveDays = 6,
        };

        var result = WizardJobRunner.MapUnfilledSlots(UnfilledSlotDiagnostics.Diagnose(context, []));

        var slot = result.ShouldHaveSingleItem();
        slot.ShiftId.ShouldBe(shiftId.ToString());
        slot.Date.ShouldBe("2026-04-22");
        slot.MissingSeats.ShouldBe(1);
        slot.FeasibleAgentCount.ShouldBe(0);
        slot.VetoCounts["BreakBlocker"].ShouldBe(2);
    }

    private static CoreAgent VacationAgent(string id) => new(
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
        WorkOnWednesday = true,
    };
}
