// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Schedules;
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
        bool isReplacementEntry = false, ApiWorkChangeType? workChangeType = null, Guid? sourceId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            EntryType = entryType,
            ClientId = clientId,
            EntryDate = Date,
            StartTime = new TimeSpan(startHour, 0, 0),
            EndTime = new TimeSpan(endHour, 0, 0),
            EntryId = new Guid("00000000-0000-0000-0000-0000000001a0"),
            SourceId = sourceId ?? Guid.NewGuid(),
            IsReplacementEntry = isReplacementEntry,
            WorkChangeType = workChangeType is null ? null : (int)workChangeType.Value,
        };

    [Test]
    public void A_work_without_a_scenario_clone_is_immutable_so_no_swap_can_touch_it()
    {
        var cloned = Cell((int)ScheduleEntryType.Work, Original, 8, 16);
        var foreign = Cell((int)ScheduleEntryType.Work, Substitute, 8, 16);

        var works = RecoverySnapshotBuilder.BuildWorks(
            [cloned, foreign], new HashSet<Guid>(), new HashSet<Guid> { cloned.SourceId }, out _);

        works[new CellKey(Original, Day)].ShouldHaveSingleItem().IsLocked.ShouldBeFalse();
        works[new CellKey(Substitute, Day)].ShouldHaveSingleItem().IsLocked.ShouldBeTrue();
    }

    [Test]
    public void A_whole_cloned_work_moves_as_a_whole()
    {
        var work = Cell((int)ScheduleEntryType.Work, Original, 8, 16);

        var works = RecoverySnapshotBuilder.BuildWorks(
            [work], new HashSet<Guid>(), new HashSet<Guid> { work.SourceId }, out _, untouchedWorkIds: new HashSet<Guid> { work.SourceId });

        works[new CellKey(Original, Day)].ShouldHaveSingleItem().MovesAsWhole.ShouldBeTrue();
    }

    [Test]
    public void A_work_something_holds_in_place_does_not_move()
    {
        var work = Cell((int)ScheduleEntryType.Work, Original, 8, 16);

        var works = RecoverySnapshotBuilder.BuildWorks(
            [work], new HashSet<Guid>(), new HashSet<Guid> { work.SourceId }, out _, untouchedWorkIds: new HashSet<Guid>());

        works[new CellKey(Original, Day)].ShouldHaveSingleItem().MovesAsWhole.ShouldBeFalse();
    }

    [Test]
    public void Only_the_named_work_is_swap_locked()
    {
        var moved = Cell((int)ScheduleEntryType.Work, Original, 8, 16);
        var plain = Cell((int)ScheduleEntryType.Work, Original, 17, 20);

        var works = RecoverySnapshotBuilder.BuildWorks(
            [moved, plain], new HashSet<Guid>(), new HashSet<Guid> { moved.SourceId, plain.SourceId }, out _,
            swapLockedWorkIds: new HashSet<Guid> { moved.SourceId });

        var day = works[new CellKey(Original, Day)];
        day.Single(w => w.WorkIds![0] == moved.SourceId).IsSwapLocked.ShouldBeTrue();
        day.Single(w => w.WorkIds![0] == moved.SourceId).IsLocked.ShouldBeFalse();
        day.Single(w => w.WorkIds![0] == plain.SourceId).IsSwapLocked.ShouldBeFalse();
    }

    [TestCase(WorkLockLevel.None, false, false)]
    [TestCase(WorkLockLevel.Confirmed, false, false)]
    [TestCase(WorkLockLevel.None, true, false)]
    [TestCase(WorkLockLevel.None, false, true)]
    public void MovesAsWhole_agrees_with_the_cover_decision(WorkLockLevel lockLevel, bool hasReplacement, bool hasLockedChild)
    {
        var work = Cell((int)ScheduleEntryType.Work, Original, 8, 16);
        work.LockLevel = (int)lockLevel;
        var start = new TimeOnly(8, 0);
        var end = new TimeOnly(16, 0);
        var facts = new CoverMoveFacts(work.SourceId, Original, null, DateOnly.MinValue, start, end, lockLevel, hasReplacement, hasLockedChild);
        var untouched = CoverMoveDecision.IsUntouched(facts) ? new HashSet<Guid> { work.SourceId } : new HashSet<Guid>();

        var works = RecoverySnapshotBuilder.BuildWorks(
            [work], new HashSet<Guid>(), new HashSet<Guid> { work.SourceId }, out _, untouchedWorkIds: untouched);

        works[new CellKey(Original, Day)].ShouldHaveSingleItem().MovesAsWhole
            .ShouldBe(CoverMoveDecision.CoversWholeWork(facts, Original, start, end));
    }

    [Test]
    public void A_work_that_cannot_be_written_does_not_move()
    {
        var work = Cell((int)ScheduleEntryType.Work, Original, 8, 16);

        var works = RecoverySnapshotBuilder.BuildWorks([work], new HashSet<Guid>(), new HashSet<Guid>(), out _);

        works[new CellKey(Original, Day)].ShouldHaveSingleItem().MovesAsWhole.ShouldBeFalse();
    }

    [Test]
    public void A_work_already_handed_over_in_part_does_not_move()
    {
        var work = Cell((int)ScheduleEntryType.Work, Original, 8, 16);
        var handedOver = Cell((int)ScheduleEntryType.WorkChange, Original, 8, 12,
            workChangeType: ApiWorkChangeType.ReplacementWithin, sourceId: work.SourceId);

        var works = RecoverySnapshotBuilder.BuildWorks(
            [work, handedOver], new HashSet<Guid>(), new HashSet<Guid> { work.SourceId }, out _);

        works[new CellKey(Original, Day)].ShouldHaveSingleItem().MovesAsWhole.ShouldBeFalse();
    }

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

        var works = RecoverySnapshotBuilder.BuildWorks(cells, new HashSet<Guid>(), cells.Select(c => c.SourceId).ToHashSet(), out var breakDays);

        // The substitute's replacement cover is present and immutable.
        works.ContainsKey(new CellKey(Substitute, Day)).ShouldBeTrue();
        var cover = works[new CellKey(Substitute, Day)].ShouldHaveSingleItem();
        cover.IsLocked.ShouldBeTrue();
        cover.Hours.ShouldBe(8m);

        // The original keeps only the real Work — the non-replacement correction WorkChange is excluded.
        works[new CellKey(Original, Day)].ShouldHaveSingleItem().Hours.ShouldBe(8m);

        // The break is recorded as a break date, not as occupancy.
        breakDays.Blocking.ShouldContain((BreakOwner, Day));
        works.ContainsKey(new CellKey(BreakOwner, Day)).ShouldBeFalse();
    }

    [Test]
    public void Original_work_is_trimmed_by_a_within_replacement_into_two_segments()
    {
        var workId = Guid.NewGuid();
        var cells = new List<ScheduleCell>
        {
            Cell((int)ScheduleEntryType.Work, Original, 8, 16, sourceId: workId),
            // The 10:00-12:00 middle is handed to a substitute (original loses that time).
            Cell((int)ScheduleEntryType.WorkChange, Original, 10, 12,
                isReplacementEntry: false, workChangeType: ApiWorkChangeType.ReplacementWithin, sourceId: workId),
        };

        var works = RecoverySnapshotBuilder.BuildWorks(cells, new HashSet<Guid>(), cells.Select(c => c.SourceId).ToHashSet(), out _);

        // The original only still works 08:00-10:00 and 12:00-16:00 → two segments, not the full shift.
        var segments = works[new CellKey(Original, Day)];
        segments.Count.ShouldBe(2);
        segments.Select(w => w.Hours).OrderBy(h => h).ShouldBe([2m, 4m]);
    }

    [Test]
    public void A_replacement_after_midnight_of_a_night_work_is_taken_off_the_work()
    {
        var workId = Guid.NewGuid();
        var nextDay = Date.AddDays(1);
        var replacement = Cell((int)ScheduleEntryType.WorkChange, Original, 2, 4,
            isReplacementEntry: false, workChangeType: ApiWorkChangeType.ReplacementWithin, sourceId: workId);
        replacement.EntryDate = nextDay;
        var cells = new List<ScheduleCell>
        {
            Cell((int)ScheduleEntryType.Work, Original, 22, 6, sourceId: workId),
            replacement,
        };

        var works = RecoverySnapshotBuilder.BuildWorks(cells, new HashSet<Guid>(), cells.Select(c => c.SourceId).ToHashSet(), out _);

        var segments = works[new CellKey(Original, Day)].OrderBy(w => w.StartAt).ToList();
        segments.Count.ShouldBe(2);
        segments[0].StartAt.ShouldBe(Date.AddHours(22));
        segments[0].EndAt.ShouldBe(nextDay.AddHours(2));
        segments[1].StartAt.ShouldBe(nextDay.AddHours(4));
        segments[1].EndAt.ShouldBe(nextDay.AddHours(6));
    }

    [Test]
    public void A_fully_replaced_original_work_leaves_no_occupancy_for_the_original()
    {
        var workId = Guid.NewGuid();
        var cells = new List<ScheduleCell>
        {
            Cell((int)ScheduleEntryType.Work, Original, 8, 16, sourceId: workId),
            // The whole shift is handed to a substitute.
            Cell((int)ScheduleEntryType.WorkChange, Original, 8, 16,
                isReplacementEntry: false, workChangeType: ApiWorkChangeType.ReplacementWithin, sourceId: workId),
            // ...and the substitute's cover appears under the substitute's id.
            Cell((int)ScheduleEntryType.WorkChange, Substitute, 8, 16, isReplacementEntry: true),
        };

        var works = RecoverySnapshotBuilder.BuildWorks(cells, new HashSet<Guid>(), cells.Select(c => c.SourceId).ToHashSet(), out _);

        // The original is fully covered → no demand would be raised for them.
        works.ContainsKey(new CellKey(Original, Day)).ShouldBeFalse();
        // The substitute carries the cover as immutable occupancy.
        works[new CellKey(Substitute, Day)].ShouldHaveSingleItem().IsLocked.ShouldBeTrue();
    }
}
