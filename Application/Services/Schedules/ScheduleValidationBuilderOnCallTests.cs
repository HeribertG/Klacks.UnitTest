// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Application.Services.Schedules;

/// <summary>
/// A call-out during on-call duty is not a collision: a work or a replacement block over an on-call break
/// is reported as an on-call-overlap Warning. Breaks of other types, break-on-break pairs and
/// work-on-work pairs stay collisions (Error).
/// </summary>
[TestFixture]
public class ScheduleValidationBuilderOnCallTests
{
    private static readonly DateOnly Day = new(2026, 3, 8);

    private Guid _clientId;
    private ClientTimeline _timeline = null!;
    private List<ScheduleValidationNotificationDto> _entries = null!;

    [SetUp]
    public void Setup()
    {
        _clientId = Guid.NewGuid();
        _timeline = new ClientTimeline(_clientId);
        _entries = [];
    }

    private ScheduleBlock Block(ScheduleBlockType type, int startHour, int endHour)
        => new(Guid.NewGuid(), type, _clientId,
            Day.ToDateTime(new TimeOnly(startHour, 0)),
            endHour == 24 ? Day.AddDays(1).ToDateTime(TimeOnly.MinValue) : Day.ToDateTime(new TimeOnly(endHour, 0)));

    private void Run(IReadOnlySet<Guid> onCallBreakIds, params ScheduleBlock[] blocks)
    {
        _timeline.AddBlocks(blocks);
        _timeline.SortBlocks();
        ScheduleValidationBuilder.AddCollisions(_entries, _timeline, "Test", onCallBreakIds);
    }

    [TestCase(ScheduleBlockType.Work)]
    [TestCase(ScheduleBlockType.Replacement)]
    public void BlockOverOnCallBreak_IsOnCallOverlapWarning(ScheduleBlockType type)
    {
        var onCall = Block(ScheduleBlockType.Break, 0, 24);

        Run(new HashSet<Guid> { onCall.SourceId }, onCall, Block(type, 10, 14));

        var entry = _entries.ShouldHaveSingleItem();
        entry.Type.ShouldBe(ScheduleValidationType.Warning);
        entry.Comment.ShouldBe(ScheduleValidationKeys.OnCallOverlap);
        entry.CommentParams["workTimeRange"].ShouldBe("10:00 - 14:00");
        entry.CommentParams["onCallTimeRange"].ShouldBe("00:00 - 00:00");
    }

    [Test]
    public void WorkOverOtherBreak_StaysCollisionError()
    {
        var sick = Block(ScheduleBlockType.Break, 0, 24);

        Run(new HashSet<Guid>(), sick, Block(ScheduleBlockType.Work, 10, 14));

        var entry = _entries.ShouldHaveSingleItem();
        entry.Type.ShouldBe(ScheduleValidationType.Error);
        entry.Comment.ShouldBe(ScheduleValidationKeys.Collision);
    }

    [Test]
    public void OnCallBreakOverOtherBreak_StaysCollisionError()
    {
        var onCall = Block(ScheduleBlockType.Break, 0, 24);

        Run(new HashSet<Guid> { onCall.SourceId }, onCall, Block(ScheduleBlockType.Break, 0, 24));

        _entries.ShouldHaveSingleItem().Comment.ShouldBe(ScheduleValidationKeys.Collision);
    }

    [Test]
    public void TwoWorksDuringOnCall_StillCollideWithEachOther()
    {
        var onCall = Block(ScheduleBlockType.Break, 0, 24);

        Run(new HashSet<Guid> { onCall.SourceId }, onCall,
            Block(ScheduleBlockType.Work, 8, 12), Block(ScheduleBlockType.Work, 10, 14));

        _entries.Count(e => e.Comment == ScheduleValidationKeys.OnCallOverlap).ShouldBe(2);
        _entries.Single(e => e.Comment == ScheduleValidationKeys.Collision).Type.ShouldBe(ScheduleValidationType.Error);
    }

    [Test]
    public void AddOnCallOverlaps_ReportsOnlyTheOnCallWarnings()
    {
        var onCall = Block(ScheduleBlockType.Break, 0, 24);
        _timeline.AddBlocks([onCall, Block(ScheduleBlockType.Work, 8, 12), Block(ScheduleBlockType.Work, 10, 14)]);
        _timeline.SortBlocks();

        ScheduleValidationBuilder.AddOnCallOverlaps(_entries, _timeline, "Test", new HashSet<Guid> { onCall.SourceId });

        _entries.Count.ShouldBe(2);
        _entries.ShouldAllBe(e => e.Comment == ScheduleValidationKeys.OnCallOverlap && e.Type == ScheduleValidationType.Warning);
    }

    [Test]
    public void WithoutOnCallBreaks_EveryBreakIsBlocking()
    {
        _timeline.AddBlocks([Block(ScheduleBlockType.Break, 0, 24), Block(ScheduleBlockType.Work, 10, 14)]);
        _timeline.SortBlocks();

        ScheduleValidationBuilder.AddCollisions(_entries, _timeline, "Test", OnCallOverlapDetector.NoOnCallBreaks);

        _entries.ShouldHaveSingleItem().Comment.ShouldBe(ScheduleValidationKeys.Collision);
    }
}
