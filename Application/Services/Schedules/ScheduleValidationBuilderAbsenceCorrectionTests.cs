// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Services;

namespace Klacks.UnitTest.Application.Services.Schedules;

/// <summary>
/// A correction, briefing or travel block of a work over an absence belongs to that work: the display paths do not
/// report it as a collision of its own (the uncovered-absence notice speaks for the work), the booking checks keep it
/// a collision. A correction block without a parent work and a replacement block over an absence stay collisions
/// everywhere.
/// </summary>
[TestFixture]
public class ScheduleValidationBuilderAbsenceCorrectionTests
{
    private static readonly DateOnly Day = new(2026, 3, 8);
    private static readonly IReadOnlySet<Guid> NoOnCall = new HashSet<Guid>();

    private Guid _clientId;
    private Guid _workId;
    private ClientTimeline _timeline = null!;

    [SetUp]
    public void Setup()
    {
        _clientId = Guid.NewGuid();
        _workId = Guid.NewGuid();
        _timeline = new ClientTimeline(_clientId);
    }

    [Test]
    public void BookingChecks_KeepACorrectionOverAnAbsenceAsACollision()
    {
        Given(Block(ScheduleBlockType.Break, 0, 24), Block(ScheduleBlockType.Correction, 7, 8, _workId));
        var entries = new List<ScheduleValidationNotificationDto>();

        ScheduleValidationBuilder.AddCollisions(entries, _timeline, "Test", NoOnCall);

        entries.ShouldHaveSingleItem().Comment.ShouldBe(ScheduleValidationKeys.Collision);
    }

    [Test]
    public void DisplayPath_DropsACorrectionOfAWorkOverAnAbsence()
    {
        Given(Block(ScheduleBlockType.Break, 0, 24), Block(ScheduleBlockType.Correction, 7, 8, _workId));
        var entries = new List<ScheduleValidationNotificationDto>();

        ScheduleValidationBuilder.AddCollisionsExceptAbsenceOverlaps(entries, _timeline, "Test", NoOnCall);

        entries.ShouldBeEmpty();
    }

    [Test]
    public void DisplayPath_KeepsACorrectionWithoutAParentWork()
    {
        Given(Block(ScheduleBlockType.Break, 0, 24), Block(ScheduleBlockType.Correction, 7, 8));
        var entries = new List<ScheduleValidationNotificationDto>();

        ScheduleValidationBuilder.AddCollisionsExceptAbsenceOverlaps(entries, _timeline, "Test", NoOnCall);

        entries.ShouldHaveSingleItem().Comment.ShouldBe(ScheduleValidationKeys.Collision);
    }

    [Test]
    public void DisplayPath_KeepsAReplacementBlockOverAnAbsence()
    {
        Given(Block(ScheduleBlockType.Break, 0, 24), Block(ScheduleBlockType.Replacement, 8, 12, _workId));
        var entries = new List<ScheduleValidationNotificationDto>();

        ScheduleValidationBuilder.AddCollisionsExceptAbsenceOverlaps(entries, _timeline, "Test", NoOnCall);

        entries.ShouldHaveSingleItem().Comment.ShouldBe(ScheduleValidationKeys.Collision);
    }

    [Test]
    public void LiveCollisionList_DropsACorrectionOfAWorkOverAnAbsence()
    {
        Given(Block(ScheduleBlockType.Break, 0, 24), Block(ScheduleBlockType.Correction, 7, 8, _workId));

        var collisions = TimelineCollisionNotificationBuilder.BuildList(_timeline, new Dictionary<Guid, string>(), NoOnCall);

        collisions.ShouldBeEmpty();
    }

    [Test]
    public void LiveCollisionList_KeepsACorrectionWithoutAParentWork()
    {
        Given(Block(ScheduleBlockType.Break, 0, 24), Block(ScheduleBlockType.Correction, 7, 8));

        var collisions = TimelineCollisionNotificationBuilder.BuildList(_timeline, new Dictionary<Guid, string>(), NoOnCall);

        collisions.ShouldHaveSingleItem();
    }

    private void Given(params ScheduleBlock[] blocks)
    {
        _timeline.AddBlocks(blocks);
        _timeline.SortBlocks();
    }

    private ScheduleBlock Block(ScheduleBlockType type, int startHour, int endHour, Guid? parentWorkId = null)
        => new(Guid.NewGuid(), type, _clientId,
            Day.ToDateTime(new TimeOnly(startHour, 0)),
            endHour == 24 ? Day.AddDays(1).ToDateTime(TimeOnly.MinValue) : Day.ToDateTime(new TimeOnly(endHour, 0)),
            ParentWorkId: parentWorkId);
}
