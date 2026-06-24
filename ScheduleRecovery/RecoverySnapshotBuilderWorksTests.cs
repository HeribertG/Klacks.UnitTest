// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.ScheduleRecovery.Model;
using NUnit.Framework;
using Shouldly;
using ApiWorkChangeType = Klacks.Api.Domain.Enums.WorkChangeType;

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
        int entryType, Guid clientId, int startHour, int endHour,
        bool isReplacementEntry = false, ApiWorkChangeType? workChangeType = null)
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
            WorkChangeType = workChangeType is null ? null : (int)workChangeType.Value,
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

    [Test]
    public void Original_work_is_trimmed_by_a_within_replacement_into_two_segments()
    {
        var cells = new List<ScheduleCell>
        {
            Cell((int)ScheduleEntryType.Work, Original, 8, 16),
            // The 10:00-12:00 middle is handed to a substitute (original loses that time).
            Cell((int)ScheduleEntryType.WorkChange, Original, 10, 12,
                isReplacementEntry: false, workChangeType: ApiWorkChangeType.ReplacementWithin),
        };

        var works = RecoverySnapshotBuilder.BuildWorks(cells, out _);

        // The original only still works 08:00-10:00 and 12:00-16:00 → two segments, not the full shift.
        var segments = works[new CellKey(Original, Day)];
        segments.Count.ShouldBe(2);
        segments.Select(w => w.Hours).OrderBy(h => h).ShouldBe([2m, 4m]);
    }

    [Test]
    public void A_fully_replaced_original_work_leaves_no_occupancy_for_the_original()
    {
        var cells = new List<ScheduleCell>
        {
            Cell((int)ScheduleEntryType.Work, Original, 8, 16),
            // The whole shift is handed to a substitute.
            Cell((int)ScheduleEntryType.WorkChange, Original, 8, 16,
                isReplacementEntry: false, workChangeType: ApiWorkChangeType.ReplacementWithin),
            // ...and the substitute's cover appears under the substitute's id.
            Cell((int)ScheduleEntryType.WorkChange, Substitute, 8, 16, isReplacementEntry: true),
        };

        var works = RecoverySnapshotBuilder.BuildWorks(cells, out _);

        // The original is fully covered → no demand would be raised for them.
        works.ContainsKey(new CellKey(Original, Day)).ShouldBeFalse();
        // The substitute carries the cover as immutable occupancy.
        works[new CellKey(Substitute, Day)].ShouldHaveSingleItem().IsLocked.ShouldBeTrue();
    }
}
