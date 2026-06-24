// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.ScheduleRecovery.Model;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// Unit tests for <see cref="RecoverySnapshotBuilder.BuildWorks"/>: a genuine replacement WorkChange
/// (is_replacement_entry = true, reported under the substitute's client id) must become immutable
/// occupancy so a borrowed substitute is never seen as free; a non-replacement WorkChange
/// (correction / travel / briefing, carrying the original client) must stay excluded.
/// </summary>
[TestFixture]
public sealed class RecoverySnapshotBuilderWorksTests
{
    private static readonly Guid Original = new("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Substitute = new("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid BreakOwner = new("00000000-0000-0000-0000-0000000000a3");
    private static readonly DateTime Date = new(2026, 6, 3);
    private static readonly DateOnly Day = new(2026, 6, 3);

    private static ScheduleCell Cell(
        int entryType, Guid clientId, int startHour, int endHour, bool isReplacementEntry = false)
        => new()
        {
            Id = Guid.NewGuid(),
            EntryType = entryType,
            ClientId = clientId,
            EntryDate = Date,
            StartTime = new TimeSpan(startHour, 0, 0),
            EndTime = new TimeSpan(endHour, 0, 0),
            EntryId = new Guid("00000000-0000-0000-0000-0000000001a0"),
            SourceId = Guid.NewGuid(),
            IsReplacementEntry = isReplacementEntry,
        };

    [Test]
    public void Replacement_workchange_becomes_immutable_occupancy_for_the_substitute()
    {
        var cells = new List<ScheduleCell>
        {
            Cell((int)ScheduleEntryType.Work, Original, 8, 16),
            Cell((int)ScheduleEntryType.WorkChange, Substitute, 14, 22, isReplacementEntry: true),
            Cell((int)ScheduleEntryType.WorkChange, Original, 6, 7, isReplacementEntry: false),
            Cell((int)ScheduleEntryType.Break, BreakOwner, 0, 0),
        };

        var works = RecoverySnapshotBuilder.BuildWorks(cells, out var breakDates);

        // The substitute's replacement cover is present and immutable.
        works.ContainsKey(new CellKey(Substitute, Day)).ShouldBeTrue();
        var cover = works[new CellKey(Substitute, Day)].ShouldHaveSingleItem();
        cover.IsLocked.ShouldBeTrue();
        cover.Hours.ShouldBe(8m);

        // The original keeps only the real Work — the non-replacement correction WorkChange is excluded.
        works[new CellKey(Original, Day)].ShouldHaveSingleItem().Hours.ShouldBe(8m);

        // The break is recorded as a break date, not as occupancy.
        breakDates.ShouldContain((BreakOwner, Day));
        works.ContainsKey(new CellKey(BreakOwner, Day)).ShouldBeFalse();
    }
}
