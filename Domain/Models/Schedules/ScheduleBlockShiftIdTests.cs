// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the optional ShiftId parameter on the ScheduleBlock record.
/// Ensures that ShiftId is transported correctly and backward compatibility is maintained.
/// </summary>
using Klacks.Api.Domain.Models.Schedules;

namespace Klacks.UnitTest.Domain.Models.Schedules;

[TestFixture]
public class ScheduleBlockShiftIdTests
{
    private static readonly DateOnly BaseDate = new(2026, 3, 15);
    private static readonly Guid ClientId = Guid.NewGuid();

    [Test]
    public void Constructor_WithoutShiftId_DefaultsToNull()
    {
        // Arrange & Act
        var block = new ScheduleBlock(
            Guid.NewGuid(),
            ScheduleBlockType.Work,
            ClientId,
            BaseDate.ToDateTime(new TimeOnly(8, 0)),
            BaseDate.ToDateTime(new TimeOnly(16, 0)));

        // Assert
        block.ShiftId.ShouldBeNull();
    }

    [Test]
    public void Constructor_WithShiftId_StoresValue()
    {
        // Arrange
        var shiftId = Guid.NewGuid();

        // Act
        var block = new ScheduleBlock(
            Guid.NewGuid(),
            ScheduleBlockType.Work,
            ClientId,
            BaseDate.ToDateTime(new TimeOnly(8, 0)),
            BaseDate.ToDateTime(new TimeOnly(16, 0)),
            shiftId);

        // Assert
        block.ShiftId.ShouldBe(shiftId);
    }

    [Test]
    public void Constructor_WithExplicitNull_ShiftIdIsNull()
    {
        // Arrange & Act
        var block = new ScheduleBlock(
            Guid.NewGuid(),
            ScheduleBlockType.Work,
            ClientId,
            BaseDate.ToDateTime(new TimeOnly(8, 0)),
            BaseDate.ToDateTime(new TimeOnly(16, 0)),
            null);

        // Assert
        block.ShiftId.ShouldBeNull();
    }

    [Test]
    public void BreakBlock_WithoutShiftId_ShiftIdIsNull()
    {
        // Arrange & Act
        var block = new ScheduleBlock(
            Guid.NewGuid(),
            ScheduleBlockType.Break,
            ClientId,
            BaseDate.ToDateTime(new TimeOnly(12, 0)),
            BaseDate.ToDateTime(new TimeOnly(13, 0)));

        // Assert
        block.ShiftId.ShouldBeNull();
        block.BlockType.ShouldBe(ScheduleBlockType.Break);
    }

    [Test]
    public void ExistingProperties_StillWorkWithShiftId()
    {
        // Arrange
        var sourceId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        var start = BaseDate.ToDateTime(new TimeOnly(8, 0));
        var end = BaseDate.ToDateTime(new TimeOnly(16, 0));

        // Act
        var block = new ScheduleBlock(sourceId, ScheduleBlockType.Work, ClientId, start, end, shiftId);

        // Assert
        block.SourceId.ShouldBe(sourceId);
        block.BlockType.ShouldBe(ScheduleBlockType.Work);
        block.ClientId.ShouldBe(ClientId);
        block.Start.ShouldBe(start);
        block.End.ShouldBe(end);
        block.ShiftId.ShouldBe(shiftId);
        block.Duration.ShouldBe(TimeSpan.FromHours(8));
        block.OwnerDate.ShouldBe(BaseDate);
    }

    [Test]
    public void GapTo_BlocksWithDifferentShiftIds_ReturnsCorrectGap()
    {
        // Arrange
        var block1 = new ScheduleBlock(
            Guid.NewGuid(), ScheduleBlockType.Work, ClientId,
            BaseDate.ToDateTime(new TimeOnly(8, 0)),
            BaseDate.ToDateTime(new TimeOnly(12, 0)),
            Guid.NewGuid());

        var block2 = new ScheduleBlock(
            Guid.NewGuid(), ScheduleBlockType.Work, ClientId,
            BaseDate.ToDateTime(new TimeOnly(14, 0)),
            BaseDate.ToDateTime(new TimeOnly(18, 0)),
            Guid.NewGuid());

        // Act
        var gap = block1.GapTo(block2);

        // Assert
        gap.ShouldBe(TimeSpan.FromHours(2));
    }
}
