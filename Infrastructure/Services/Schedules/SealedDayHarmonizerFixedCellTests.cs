// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A day sealed for an agent is a fixed cell for the Harmonizer family (Wizard 2, Holistic/Wizard 3, Wizard 4,
/// AutoWizard): HarmonizerContextBuilder.BuildAssignments adds a locked zero-hour Free cell for it and locks any work
/// of that day, so no move can place anyone there and an apply never runs into the day lock.
/// </summary>
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class SealedDayHarmonizerFixedCellTests
{
    private static readonly DateOnly SealedDate = new(2026, 4, 22);

    [Test]
    public void SealedDayBlocksPlacement_EmptySealedDay_BecomesLockedZeroHourCell()
    {
        var agent = Guid.NewGuid();

        var assignments = HarmonizerContextBuilder.BuildAssignments(
            [], [], [], new HashSet<Guid> { agent }, new HashSet<(Guid ClientId, DateOnly Date)> { (agent, SealedDate) });

        var cell = assignments.ShouldHaveSingleItem();
        cell.AgentId.ShouldBe(agent.ToString());
        cell.Date.ShouldBe(SealedDate);
        cell.IsLocked.ShouldBeTrue();
        cell.Hours.ShouldBe(0m);
        cell.WorkIds.ShouldBeEmpty();
        cell.Symbol.ShouldBe(CellSymbol.Free);
    }

    [Test]
    public void SealedDayBlocksPlacement_UnlockedWorkOnSealedDay_IsLocked()
    {
        var agent = Guid.NewGuid();
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = agent,
            ShiftId = Guid.NewGuid(),
            CurrentDate = SealedDate,
            StartTime = new TimeOnly(6, 0),
            EndTime = new TimeOnly(14, 0),
            WorkTime = 8m,
            LockLevel = WorkLockLevel.None,
        };

        var assignments = HarmonizerContextBuilder.BuildAssignments(
            [work], [], [], new HashSet<Guid> { agent }, new HashSet<(Guid ClientId, DateOnly Date)> { (agent, SealedDate) });

        assignments.Single(a => a.WorkIds.Contains(work.Id)).IsLocked.ShouldBeTrue();
        assignments.Where(a => a.WorkIds.Count == 0).ShouldHaveSingleItem().Hours.ShouldBe(0m);
    }

    [Test]
    public void SealedDayOfAgentOutsideTheRun_IsNotAdded()
    {
        var assignments = HarmonizerContextBuilder.BuildAssignments(
            [], [], [], new HashSet<Guid> { Guid.NewGuid() }, new HashSet<(Guid ClientId, DateOnly Date)> { (Guid.NewGuid(), SealedDate) });

        assignments.ShouldBeEmpty();
    }
}