// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.ScheduleRecovery.Model;
using NUnit.Framework;
using Shouldly;
using ApiWorkChangeType = Klacks.Api.Domain.Enums.WorkChangeType;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// Unit tests for the lead, trail and extra paid hours <see cref="RecoverySnapshotBuilder.BuildWorks"/> reads from the
/// non-replacement work changes: a briefing carries occupied time but no paid hours, travel and corrections carry both,
/// and a segment of a partly replaced work inherits the lead only at the start of the shift and the trail only at its end.
/// </summary>
[TestFixture]
public sealed class RecoverySnapshotBuilderPaddingTests
{
    private static readonly Guid Original = new("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid WorkId = new("00000000-0000-0000-0000-0000000000b1");
    private static readonly DateTime Date = new(2026, 6, 3);
    private static readonly DateOnly Day = new(2026, 6, 3);

    private static ScheduleCell WorkCell(int startHour, int endHour) => new()
    {
        Id = Guid.NewGuid(),
        EntryType = (int)ScheduleEntryType.Work,
        ClientId = Original,
        EntryDate = Date,
        StartTime = new TimeSpan(startHour, 0, 0),
        EndTime = new TimeSpan(endHour, 0, 0),
        EntryId = new Guid("00000000-0000-0000-0000-0000000001a0"),
        SourceId = WorkId,
    };

    private static ScheduleCell Change(ApiWorkChangeType type, decimal hours, int startHour = 0, int endHour = 0) => new()
    {
        Id = Guid.NewGuid(),
        EntryType = (int)ScheduleEntryType.WorkChange,
        ClientId = Original,
        EntryDate = Date,
        StartTime = new TimeSpan(startHour, 0, 0),
        EndTime = new TimeSpan(endHour, 0, 0),
        EntryId = new Guid("00000000-0000-0000-0000-0000000001a0"),
        SourceId = WorkId,
        ChangeTime = hours,
        WorkChangeType = (int)type,
    };

    [Test]
    public void Lead_trail_and_extra_paid_hours_are_summed_per_work()
    {
        var cells = new List<ScheduleCell>
        {
            WorkCell(8, 16),
            Change(ApiWorkChangeType.Briefing, 1m),
            Change(ApiWorkChangeType.TravelStart, 0.75m),
            Change(ApiWorkChangeType.Debriefing, 0.5m),
            Change(ApiWorkChangeType.CorrectionEnd, 2m),
            Change(ApiWorkChangeType.TravelWithin, 0.25m, 11, 12),
        };

        var works = RecoverySnapshotBuilder.BuildWorks(cells, new HashSet<Guid>(), cells.Select(c => c.SourceId).ToHashSet(), out _);

        var work = works[new CellKey(Original, Day)].ShouldHaveSingleItem();
        work.LeadHours.ShouldBe(1.75m);
        work.TrailHours.ShouldBe(2.5m);
        work.ExtraPaidHours.ShouldBe(3m);
        work.Hours.ShouldBe(8m);
    }

    [Test]
    public void A_briefing_and_a_debriefing_alone_carry_no_paid_hours()
    {
        var cells = new List<ScheduleCell>
        {
            WorkCell(8, 16),
            Change(ApiWorkChangeType.Briefing, 1m),
            Change(ApiWorkChangeType.Debriefing, 0.5m),
        };

        var works = RecoverySnapshotBuilder.BuildWorks(cells, new HashSet<Guid>(), cells.Select(c => c.SourceId).ToHashSet(), out _);

        var work = works[new CellKey(Original, Day)].ShouldHaveSingleItem();
        work.LeadHours.ShouldBe(1m);
        work.TrailHours.ShouldBe(0.5m);
        work.ExtraPaidHours.ShouldBe(0m);
    }

    [Test]
    public void Work_changes_of_another_work_do_not_pad_this_one()
    {
        var other = Change(ApiWorkChangeType.Briefing, 1m);
        other.SourceId = Guid.NewGuid();
        var cells = new List<ScheduleCell> { WorkCell(8, 16), other };

        var works = RecoverySnapshotBuilder.BuildWorks(cells, new HashSet<Guid>(), cells.Select(c => c.SourceId).ToHashSet(), out _);

        var work = works[new CellKey(Original, Day)].ShouldHaveSingleItem();
        work.LeadHours.ShouldBe(0m);
        work.TrailHours.ShouldBe(0m);
    }

    [Test]
    public void Segments_inherit_the_lead_only_at_the_shift_start_and_the_trail_only_at_the_shift_end()
    {
        var cells = new List<ScheduleCell>
        {
            WorkCell(8, 16),
            Change(ApiWorkChangeType.ReplacementWithin, 2m, 10, 12),
            Change(ApiWorkChangeType.Briefing, 1m),
            Change(ApiWorkChangeType.Debriefing, 0.5m),
            Change(ApiWorkChangeType.CorrectionStart, 1m),
            Change(ApiWorkChangeType.CorrectionEnd, 2m),
            Change(ApiWorkChangeType.TravelWithin, 0.25m, 11, 12),
        };

        var works = RecoverySnapshotBuilder.BuildWorks(cells, new HashSet<Guid>(), cells.Select(c => c.SourceId).ToHashSet(), out _);

        var segments = works[new CellKey(Original, Day)];
        segments.Count.ShouldBe(2);
        var first = segments.Single(w => w.StartAt.Hour == 8);
        var second = segments.Single(w => w.StartAt.Hour == 12);
        first.LeadHours.ShouldBe(2m);
        first.TrailHours.ShouldBe(0m);
        first.ExtraPaidHours.ShouldBe(1.25m);
        second.LeadHours.ShouldBe(0m);
        second.TrailHours.ShouldBe(2.5m);
        second.ExtraPaidHours.ShouldBe(2m);
    }

    [Test]
    public void The_trail_on_the_day_after_the_merged_cells_is_read_from_the_unmerged_cells()
    {
        var debriefing = Change(ApiWorkChangeType.Debriefing, 1.5m);
        debriefing.EntryDate = Date.AddDays(1);
        var merged = new List<ScheduleCell> { WorkCell(8, 16) };
        var all = new List<ScheduleCell> { WorkCell(8, 16), debriefing };

        var withoutAll = RecoverySnapshotBuilder.BuildWorks(merged, new HashSet<Guid>(), merged.Select(c => c.SourceId).ToHashSet(), out _);
        var withAll = RecoverySnapshotBuilder.BuildWorks(merged, new HashSet<Guid>(), merged.Select(c => c.SourceId).ToHashSet(), out _, all);

        withoutAll[new CellKey(Original, Day)].ShouldHaveSingleItem().TrailHours.ShouldBe(0m);
        withAll[new CellKey(Original, Day)].ShouldHaveSingleItem().TrailHours.ShouldBe(1.5m);
    }

    [Test]
    public void A_replacement_cover_of_the_substitute_carries_no_padding()
    {
        var substitute = new Guid("00000000-0000-0000-0000-0000000000a2");
        var cover = Change(ApiWorkChangeType.ReplacementWithin, 8m, 8, 16);
        cover.ClientId = substitute;
        cover.IsReplacementEntry = true;
        var cells = new List<ScheduleCell> { WorkCell(8, 16), Change(ApiWorkChangeType.Briefing, 1m), cover };

        var works = RecoverySnapshotBuilder.BuildWorks(cells, new HashSet<Guid>(), cells.Select(c => c.SourceId).ToHashSet(), out _);

        var covered = works[new CellKey(substitute, Day)].ShouldHaveSingleItem();
        covered.LeadHours.ShouldBe(0m);
        covered.TrailHours.ShouldBe(0m);
        covered.ExtraPaidHours.ShouldBe(0m);
    }
}
