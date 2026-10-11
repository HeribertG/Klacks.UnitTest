// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the recovery snapshot's merge of the real plan with a source scenario: the members of the source scenario's
/// group get the source scenario's plan on its days; a borrowed employee keeps the real home-group works and absences and
/// only the works on the source scenario's shifts are taken from the scenario, without counting anything twice; outside
/// the source range everything stays the real plan.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleRecovery;

[TestFixture]
public sealed class ScenarioSourceRowMergerTests
{
    private static readonly DateOnly Day = new(2026, 3, 10);
    private static readonly DateOnly OutsideDay = new(2026, 3, 20);
    private static readonly Guid Member = Guid.NewGuid();
    private static readonly Guid Borrowed = Guid.NewGuid();
    private static readonly Guid OwnedRootShift = Guid.NewGuid();
    private static readonly Guid OwnedCloneShift = Guid.NewGuid();
    private static readonly Guid HomeShift = Guid.NewGuid();
    private static readonly Guid VacationAbsence = Guid.NewGuid();

    private static readonly ScenarioSourceScope Scope = new(
        Guid.NewGuid(), Day, Day.AddDays(6), Guid.NewGuid(), new HashSet<Guid> { OwnedRootShift, OwnedCloneShift });

    [Test]
    public void Member_GetsTheSourceScenarioPlanOnItsDays()
    {
        var real = new[] { Work(Member, Day, OwnedRootShift), Work(Member, Day, HomeShift), Break(Member, Day) };
        var scenario = new[] { Work(Member, Day, OwnedCloneShift) };

        var merged = Merge(real, scenario);

        merged.ShouldBe(scenario);
    }

    [Test]
    public void BorrowedEmployee_KeepsHomeWorkAndVacation_AndTakesOnlyTheOwnedShiftsFromTheScenario()
    {
        var homeWork = Work(Borrowed, Day, HomeShift);
        var vacation = Break(Borrowed, Day);
        var realOnOwnedShift = Work(Borrowed, Day, OwnedRootShift);
        var scenarioCover = Cover(Borrowed, Day, OwnedCloneShift);
        var scenarioCopyOfVacation = Break(Borrowed, Day);

        var merged = Merge([homeWork, vacation, realOnOwnedShift], [scenarioCover, scenarioCopyOfVacation]);

        merged.ShouldBe(new[] { homeWork, vacation, scenarioCover }, ignoreOrder: true);
    }

    [Test]
    public void OutsideTheSourceRange_EverythingIsTheRealPlan()
    {
        var real = new[] { Work(Member, OutsideDay, OwnedRootShift), Work(Borrowed, OutsideDay, OwnedRootShift) };
        var scenario = new[] { Work(Member, OutsideDay, OwnedCloneShift), Cover(Borrowed, OutsideDay, OwnedCloneShift) };

        var merged = Merge(real, scenario);

        merged.ShouldBe(real);
    }

    [Test]
    public void UngroupedSource_OwnsEveryEmployeesPlanOnItsDays()
    {
        var real = new[] { Work(Borrowed, Day, HomeShift), Break(Borrowed, Day) };
        var scenario = new[] { Work(Borrowed, Day, OwnedCloneShift) };

        var merged = ScenarioSourceRowMerger.Merge(
            real, scenario, Scope, null, c => c.ClientId, c => DateOnly.FromDateTime(c.EntryDate), IsOnOwnedShift);

        merged.ShouldBe(scenario);
    }

    private static List<ScheduleCell> Merge(IReadOnlyList<ScheduleCell> real, IReadOnlyList<ScheduleCell> scenario)
        => ScenarioSourceRowMerger.Merge(
            real, scenario, Scope, [Member], c => c.ClientId, c => DateOnly.FromDateTime(c.EntryDate), IsOnOwnedShift);

    private static bool IsOnOwnedShift(ScheduleCell cell)
        => cell.EntryType != (int)ScheduleEntryType.Break && Scope.OwnedShiftIds.Contains(cell.EntryId);

    private static ScheduleCell Work(Guid clientId, DateOnly date, Guid shiftId)
        => Cell(clientId, date, ScheduleEntryType.Work, shiftId);

    private static ScheduleCell Cover(Guid clientId, DateOnly date, Guid shiftId)
    {
        var cell = Cell(clientId, date, ScheduleEntryType.WorkChange, shiftId);
        cell.IsReplacementEntry = true;
        return cell;
    }

    private static ScheduleCell Break(Guid clientId, DateOnly date)
        => Cell(clientId, date, ScheduleEntryType.Break, VacationAbsence);

    private static ScheduleCell Cell(Guid clientId, DateOnly date, ScheduleEntryType type, Guid entryId) => new()
    {
        Id = Guid.NewGuid(),
        EntryType = (int)type,
        ClientId = clientId,
        EntryDate = date.ToDateTime(TimeOnly.MinValue),
        StartTime = new TimeSpan(8, 0, 0),
        EndTime = new TimeSpan(16, 0, 0),
        EntryId = entryId,
        SourceId = Guid.NewGuid(),
    };
}
