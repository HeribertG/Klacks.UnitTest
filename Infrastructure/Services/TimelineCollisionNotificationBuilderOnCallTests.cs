// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Services;

namespace Klacks.UnitTest.Infrastructure.Services;

/// <summary>
/// The live collision list must agree with the validation builders: a work over an on-call break is not a red
/// collision (the live check reports it as an on-call-overlap Warning instead), while a work over any other
/// break and two overlapping works stay collisions.
/// </summary>
[TestFixture]
public class TimelineCollisionNotificationBuilderOnCallTests
{
    private static readonly DateOnly Day = new(2026, 3, 8);

    private readonly Guid _clientId = Guid.NewGuid();

    private ScheduleBlock Block(ScheduleBlockType type, int startHour, int endHour)
        => new(Guid.NewGuid(), type, _clientId,
            Day.ToDateTime(new TimeOnly(startHour, 0)),
            endHour == 24 ? Day.AddDays(1).ToDateTime(TimeOnly.MinValue) : Day.ToDateTime(new TimeOnly(endHour, 0)));

    private ClientTimeline Timeline(params ScheduleBlock[] blocks)
    {
        var timeline = new ClientTimeline(_clientId);
        timeline.AddBlocks(blocks);
        timeline.SortBlocks();
        return timeline;
    }

    [Test]
    public void WorkOverOnCallBreak_IsNotACollision()
    {
        var onCall = Block(ScheduleBlockType.Break, 0, 24);

        var list = TimelineCollisionNotificationBuilder.BuildList(
            Timeline(onCall, Block(ScheduleBlockType.Work, 10, 14)), [], new HashSet<Guid> { onCall.SourceId });

        list.ShouldBeEmpty();
    }

    [Test]
    public void WorkOverOtherBreak_StaysACollision()
    {
        var list = TimelineCollisionNotificationBuilder.BuildList(
            Timeline(Block(ScheduleBlockType.Break, 0, 24), Block(ScheduleBlockType.Work, 10, 14)), [], new HashSet<Guid>());

        list.ShouldHaveSingleItem();
    }

    [Test]
    public void TwoWorksDuringOnCall_StillCollide()
    {
        var onCall = Block(ScheduleBlockType.Break, 0, 24);
        var first = Block(ScheduleBlockType.Work, 8, 12);
        var second = Block(ScheduleBlockType.Work, 10, 14);

        var notification = TimelineCollisionNotificationBuilder.BuildNotification(
            Timeline(onCall, first, second), [], false, _clientId, Day, null, new HashSet<Guid> { onCall.SourceId });

        var collision = notification.Collisions.ShouldHaveSingleItem();
        new[] { collision.WorkId1, collision.WorkId2 }.ShouldBe([first.SourceId, second.SourceId], ignoreOrder: true);
    }
}
