// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Models.Associations;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleRecovery.Model;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// DayAvailability has carried HasFreeCommand, RequiredCategory and ForbiddenCategory all along, but the
/// recovery builder never filled them: a FREE day or an ONLY-LATE restriction was invisible, so recovery
/// could propose exactly the replacement the wizards would have refused. These tests pin the mapping.
/// </summary>
[TestFixture]
public sealed class RecoverySnapshotBuilderKeywordTests
{
    private static readonly Guid AgentA = new("00000000-0000-0000-0000-0000000000c1");
    private static readonly DateOnly Day = new(2026, 6, 3);

    [Test]
    public void BuildAvailability_FreeCommand_ClosesTheDay()
    {
        var availability = Build(ScheduleCommandKeyword.Free);

        availability[new CellKey(AgentA, Day)].HasFreeCommand.ShouldBeTrue();
        availability[new CellKey(AgentA, Day)].IsAvailable.ShouldBeFalse();
    }

    [TestCase(ScheduleCommandKeyword.OnlyEarly, ShiftCategory.Early)]
    [TestCase(ScheduleCommandKeyword.OnlyLate, ShiftCategory.Late)]
    [TestCase(ScheduleCommandKeyword.OnlyNight, ShiftCategory.Night)]
    public void BuildAvailability_OnlyKeyword_DemandsThatCategory(
        ScheduleCommandKeyword keyword, ShiftCategory expected)
    {
        var cell = Build(keyword)[new CellKey(AgentA, Day)];

        cell.RequiredCategory.ShouldBe(expected);
        cell.ForbiddenCategory.ShouldBeNull();
    }

    [TestCase(ScheduleCommandKeyword.NoEarly, ShiftCategory.Early)]
    [TestCase(ScheduleCommandKeyword.NoLate, ShiftCategory.Late)]
    [TestCase(ScheduleCommandKeyword.NoNight, ShiftCategory.Night)]
    public void BuildAvailability_NoKeyword_RulesOutThatCategory(
        ScheduleCommandKeyword keyword, ShiftCategory expected)
    {
        var cell = Build(keyword)[new CellKey(AgentA, Day)];

        cell.ForbiddenCategory.ShouldBe(expected);
        cell.RequiredCategory.ShouldBeNull();
    }

    [Test]
    public void BuildAvailability_NotFreeKeyword_NarrowsNothing()
    {
        // NotFree only opens a day, exactly as the harmonizer treats it.
        var cell = Build(ScheduleCommandKeyword.NotFree)[new CellKey(AgentA, Day)];

        cell.HasFreeCommand.ShouldBeFalse();
        cell.RequiredCategory.ShouldBeNull();
        cell.ForbiddenCategory.ShouldBeNull();
    }

    [Test]
    public void BuildAvailability_DayWithoutKeyword_StaysOpen()
    {
        var availability = RecoverySnapshotBuilder.BuildAvailability(
            [AgentA], Contracts(), new HashSet<(Guid, DateOnly)>(), Keywords(null), Day, Day);

        var cell = availability[new CellKey(AgentA, Day)];
        cell.HasFreeCommand.ShouldBeFalse();
        cell.RequiredCategory.ShouldBeNull();
        cell.ForbiddenCategory.ShouldBeNull();
    }

    [Test]
    public void BuildAvailability_BreakDay_StaysBlockedRegardlessOfKeyword()
    {
        var availability = RecoverySnapshotBuilder.BuildAvailability(
            [AgentA], Contracts(), new HashSet<(Guid, DateOnly)> { (AgentA, Day) },
            Keywords(ScheduleCommandKeyword.NotFree), Day, Day);

        availability[new CellKey(AgentA, Day)].HasBreakBlocker.ShouldBeTrue();
        availability[new CellKey(AgentA, Day)].IsAvailable.ShouldBeFalse();
    }

    private static Dictionary<CellKey, DayAvailability> Build(ScheduleCommandKeyword keyword)
        => RecoverySnapshotBuilder.BuildAvailability(
            [AgentA], Contracts(), new HashSet<(Guid, DateOnly)>(), Keywords(keyword), Day, Day);

    private static Dictionary<Guid, EffectiveContractData> Contracts()
        => new() { [AgentA] = new EffectiveContractData { HasActiveContract = false } };

    private static Dictionary<(Guid AgentId, DateOnly Date), ScheduleCommandKeyword> Keywords(
        ScheduleCommandKeyword? keyword)
        => keyword is null
            ? []
            : new Dictionary<(Guid, DateOnly), ScheduleCommandKeyword> { [(AgentA, Day)] = keyword.Value };
}
