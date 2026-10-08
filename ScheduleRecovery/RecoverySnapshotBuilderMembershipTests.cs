// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleRecovery.Model;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// M6: a day outside the agent's company membership is closed for recovery, also when an on-call break would otherwise
/// open it; inside the membership the on-call rules stay unchanged. K16: the preference sets take the inherited
/// preferences as they come from the scope query (a blacklist on an order bars its cut pieces).
/// </summary>
[TestFixture]
public sealed class RecoverySnapshotBuilderMembershipTests
{
    private static readonly Guid AgentA = new("00000000-0000-0000-0000-0000000000d1");
    private static readonly Guid OnCallAbsence = new("00000000-0000-0000-0000-0000000000e1");
    private static readonly DateOnly Saturday = new(2026, 3, 14);
    private static readonly DateOnly Sunday = new(2026, 3, 15);
    private static readonly DateOnly Monday = new(2026, 3, 16);

    [Test]
    public void DayAfterTheMembershipEnded_IsClosed()
    {
        var cells = Availability(new MembershipWindow(new DateOnly(2020, 1, 1), Saturday), [], Saturday, Monday);

        cells[new CellKey(AgentA, Saturday)].IsAvailable.ShouldBeTrue("the last member day stays open");
        cells[new CellKey(AgentA, Monday)].IsAvailable.ShouldBeFalse("after the exit the agent cannot be planned");
        cells[new CellKey(AgentA, Monday)].WorksOnDay.ShouldBeFalse();
    }

    [Test]
    public void DayBeforeTheMembershipStarted_IsClosed()
    {
        var cells = Availability(new MembershipWindow(Monday, null), [], Saturday, Monday);

        cells[new CellKey(AgentA, Saturday)].IsAvailable.ShouldBeFalse();
        cells[new CellKey(AgentA, Monday)].IsAvailable.ShouldBeTrue("the first member day is open");
    }

    [Test]
    public void OnCallBreakOutsideTheMembership_DoesNotOpenTheDay()
    {
        var cells = Availability(new MembershipWindow(new DateOnly(2020, 1, 1), Saturday), [OnCallBreak(Sunday)], Sunday, Sunday);

        var cell = cells[new CellKey(AgentA, Sunday)];
        cell.IsOnCall.ShouldBeFalse("an agent who left is not reachable on call");
        cell.IsAvailable.ShouldBeFalse();
    }

    [Test]
    public void OnCallBreakInsideTheMembership_StillOverridesTheContractOffDay()
    {
        var cells = Availability(new MembershipWindow(new DateOnly(2020, 1, 1), null), [OnCallBreak(Sunday)], Sunday, Sunday);

        var cell = cells[new CellKey(AgentA, Sunday)];
        cell.IsOnCall.ShouldBeTrue();
        cell.WorksOnDay.ShouldBeTrue("the on-call duty still lifts the contract's free Sunday");
        cell.IsAvailable.ShouldBeTrue();
    }

    [Test]
    public void AgentWithoutMembershipRow_IsUnrestricted()
    {
        RecoverySnapshotBuilder.BuildWorks([], new HashSet<Guid> { OnCallAbsence }, out var breakDays);

        var cells = RecoverySnapshotBuilder.BuildAvailability(
            [AgentA], AllDaysContract(), breakDays, NoKeywords(), Saturday, Monday, new Dictionary<Guid, MembershipWindow>());

        cells.Values.ShouldAllBe(cell => cell.IsAvailable);
    }

    [Test]
    public void PreferenceSets_KeepInheritedBlacklistsOfCutPieces()
    {
        var order = Guid.NewGuid();
        var piece = Guid.NewGuid();
        var preferredShift = Guid.NewGuid();

        var (preferred, blacklisted) = RecoverySnapshotBuilder.PreferenceSets(
        [
            new ScopedShiftPreference(AgentA, order, ShiftPreferenceType.Blacklist),
            new ScopedShiftPreference(AgentA, piece, ShiftPreferenceType.Blacklist),
            new ScopedShiftPreference(AgentA, preferredShift, ShiftPreferenceType.Preferred),
        ]);

        blacklisted[AgentA].ShouldBe([order, piece], ignoreOrder: true);
        preferred[AgentA].ShouldBe([preferredShift]);
    }

    private static Dictionary<CellKey, DayAvailability> Availability(
        MembershipWindow window, List<ScheduleCell> cells, DateOnly from, DateOnly until)
    {
        RecoverySnapshotBuilder.BuildWorks(cells, new HashSet<Guid> { OnCallAbsence }, out var breakDays);
        var contract = cells.Count == 0 ? AllDaysContract() : WeekdayContract();
        return RecoverySnapshotBuilder.BuildAvailability(
            [AgentA], contract, breakDays, NoKeywords(), from, until, new Dictionary<Guid, MembershipWindow> { [AgentA] = window });
    }

    private static Dictionary<(Guid AgentId, DateOnly Date), ScheduleCommandKeyword> NoKeywords() => new();

    private static ScheduleCell OnCallBreak(DateOnly date) => new()
    {
        Id = Guid.NewGuid(),
        EntryType = (int)ScheduleEntryType.Break,
        ClientId = AgentA,
        EntryDate = date.ToDateTime(TimeOnly.MinValue),
        StartTime = TimeSpan.Zero,
        EndTime = TimeSpan.Zero,
        EntryId = OnCallAbsence,
        SourceId = Guid.NewGuid(),
    };

    private static Dictionary<Guid, EffectiveContractData> AllDaysContract()
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
