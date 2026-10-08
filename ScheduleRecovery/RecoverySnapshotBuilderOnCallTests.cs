// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleRecovery.Model;
using Klacks.UnitTest.TestHelpers;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// An on-call (Pikett) absence means "reachable", not "absent": the snapshot builder must leave the day
/// open, mark it IsOnCall and lift the contract's day-of-week gate for that day only. Any other absence on
/// the same day still blocks, and a FREE keyword still closes the day.
/// </summary>
[TestFixture]
public sealed class RecoverySnapshotBuilderOnCallTests
{
    private static readonly Guid AgentA = new("00000000-0000-0000-0000-0000000000d1");
    private static readonly Guid OnCallAbsence = new("00000000-0000-0000-0000-0000000000e1");
    private static readonly Guid SickAbsence = new("00000000-0000-0000-0000-0000000000e2");
    private static readonly DateTime Date = new(2026, 6, 7);
    private static readonly DateOnly Sunday = new(2026, 6, 7);

    private static readonly IReadOnlySet<Guid> OnCallIds = new HashSet<Guid> { OnCallAbsence };

    [Test]
    public void BuildWorks_SortsOnCallAndBlockingBreaksApart()
    {
        var works = RecoverySnapshotBuilder.BuildWorks(
            [BreakCell(OnCallAbsence)], OnCallIds, out var breakDays);

        breakDays.OnCall.ShouldContain((AgentA, Sunday));
        breakDays.Blocking.ShouldBeEmpty();
        works.ContainsKey(new CellKey(AgentA, Sunday)).ShouldBeFalse();
    }

    [Test]
    public void BuildWorks_NonOnCallBreak_StaysBlocking()
    {
        RecoverySnapshotBuilder.BuildWorks([BreakCell(SickAbsence)], OnCallIds, out var breakDays);

        breakDays.Blocking.ShouldContain((AgentA, Sunday));
        breakDays.OnCall.ShouldBeEmpty();
    }

    [Test]
    public void OnCallBreak_LeavesTheDayAvailable_AndMarksIt()
    {
        var cell = Availability([BreakCell(OnCallAbsence)], NoContract());

        cell.IsAvailable.ShouldBeTrue();
        cell.IsOnCall.ShouldBeTrue();
        cell.HasBreakBlocker.ShouldBeFalse();
    }

    [Test]
    public void NormalBreak_StillBlocks()
    {
        var cell = Availability([BreakCell(SickAbsence)], NoContract());

        cell.IsAvailable.ShouldBeFalse();
        cell.HasBreakBlocker.ShouldBeTrue();
        cell.IsOnCall.ShouldBeFalse();
    }

    [Test]
    public void OnCallAndSickOnTheSameDay_SicknessWins()
    {
        var cell = Availability([BreakCell(OnCallAbsence), BreakCell(SickAbsence)], NoContract());

        cell.IsAvailable.ShouldBeFalse();
        cell.HasBreakBlocker.ShouldBeTrue();
        cell.IsOnCall.ShouldBeFalse();
    }

    [Test]
    public void OnCallOnSunday_OverridesAContractWithoutSundays()
    {
        var cell = Availability([BreakCell(OnCallAbsence)], WeekdayContract());

        cell.WorksOnDay.ShouldBeTrue();
        cell.IsAvailable.ShouldBeTrue();
    }

    [Test]
    public void SundayWithoutOnCall_StaysClosedForAWeekdayContract()
    {
        var cell = Availability([], WeekdayContract());

        cell.WorksOnDay.ShouldBeFalse();
        cell.IsAvailable.ShouldBeFalse();
        cell.IsOnCall.ShouldBeFalse();
    }

    [Test]
    public void OnCallWithFreeKeyword_StaysBlocked()
    {
        RecoverySnapshotBuilder.BuildWorks([BreakCell(OnCallAbsence)], OnCallIds, out var breakDays);
        var keywords = new Dictionary<(Guid AgentId, DateOnly Date), ScheduleCommandKeyword>
        {
            [(AgentA, Sunday)] = ScheduleCommandKeyword.Free
        };

        var cell = RecoverySnapshotBuilder.BuildAvailability(
            [AgentA], NoContract(), breakDays, keywords, Sunday, Sunday)[new CellKey(AgentA, Sunday)];

        cell.HasFreeCommand.ShouldBeTrue();
        cell.IsAvailable.ShouldBeFalse();
    }

    [Test]
    public void OnCallWithOnlyEarlyAndOnlyLateOnTheSameDay_StaysBlocked()
    {
        RecoverySnapshotBuilder.BuildWorks([BreakCell(OnCallAbsence)], OnCallIds, out var breakDays);
        var commands = new List<ScheduleCommand>
        {
            new() { ClientId = AgentA, CurrentDate = Sunday, CommandKeyword = "EARLY" },
            new() { ClientId = AgentA, CurrentDate = Sunday, CommandKeyword = "LATE" },
        };
        var keywords = RecoverySnapshotBuilder.ExtractKeywordDays(
            commands, ScheduleCommandKeywordMapper.BuildMap(ScheduleCommandKeywordTestFactory.Default));

        var cell = RecoverySnapshotBuilder.BuildAvailability(
            [AgentA], WeekdayContract(), breakDays, keywords, Sunday, Sunday)[new CellKey(AgentA, Sunday)];

        cell.IsOnCall.ShouldBeTrue("the on-call duty itself is not lost");
        cell.WorksOnDay.ShouldBeTrue("the on-call duty still lifts the contract's free Sunday");
        cell.HasFreeCommand.ShouldBeTrue("only early and only late on one day leave no shift kind");
        cell.IsAvailable.ShouldBeFalse("contradictory planning wishes close the day even on an on-call day");
    }

    private static DayAvailability Availability(
        List<ScheduleCell> cells, Dictionary<Guid, EffectiveContractData> contracts)
    {
        RecoverySnapshotBuilder.BuildWorks(cells, OnCallIds, out var breakDays);
        return RecoverySnapshotBuilder.BuildAvailability(
            [AgentA], contracts, breakDays,
            new Dictionary<(Guid AgentId, DateOnly Date), ScheduleCommandKeyword>(), Sunday, Sunday)[new CellKey(AgentA, Sunday)];
    }

    private static ScheduleCell BreakCell(Guid absenceId) => new()
    {
        Id = Guid.NewGuid(),
        EntryType = (int)ScheduleEntryType.Break,
        ClientId = AgentA,
        EntryDate = Date,
        StartTime = TimeSpan.Zero,
        EndTime = TimeSpan.Zero,
        EntryId = absenceId,
        SourceId = Guid.NewGuid(),
    };

    private static Dictionary<Guid, EffectiveContractData> NoContract()
        => new() { [AgentA] = new EffectiveContractData { HasActiveContract = false } };

    private static Dictionary<Guid, EffectiveContractData> WeekdayContract()
        => new()
        {
            [AgentA] = new EffectiveContractData
            {
                HasActiveContract = true,
                WorkOnMonday = true,
                WorkOnTuesday = true,
                WorkOnWednesday = true,
                WorkOnThursday = true,
                WorkOnFriday = true,
                WorkOnSaturday = false,
                WorkOnSunday = false,
            }
        };
}
