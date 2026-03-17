using Klacks.Api.Domain.Models.Schedules;

namespace Klacks.UnitTest.Domain.Models.Schedules;

[TestFixture]
public class ScheduleBlockTests
{
    private static readonly DateOnly BaseDate = new(2026, 3, 15);
    private static readonly Guid ClientId = Guid.NewGuid();

    private ScheduleBlock CreateBlock(int startHour, int startMin, int endHour, int endMin, DateOnly? date = null)
    {
        var d = date ?? BaseDate;
        var start = d.ToDateTime(new TimeOnly(startHour, startMin));
        var end = endHour < startHour || (endHour == startHour && endMin <= startMin)
            ? d.AddDays(1).ToDateTime(new TimeOnly(endHour, endMin))
            : d.ToDateTime(new TimeOnly(endHour, endMin));
        return new ScheduleBlock(Guid.NewGuid(), ScheduleBlockType.Work, ClientId, start, end);
    }

    [Test]
    public void Duration_NormalDayShift_ReturnsCorrectDuration()
    {
        // Arrange
        var block = CreateBlock(8, 0, 16, 0);

        // Act
        var duration = block.Duration;

        // Assert
        duration.Should().Be(TimeSpan.FromHours(8));
    }

    [Test]
    public void Duration_NightShiftOverMidnight_ReturnsCorrectDuration()
    {
        // Arrange
        var block = CreateBlock(22, 0, 6, 0);

        // Act
        var duration = block.Duration;

        // Assert
        duration.Should().Be(TimeSpan.FromHours(8));
    }

    [Test]
    public void Duration_ShortShift_ReturnsCorrectDuration()
    {
        // Arrange
        var block = CreateBlock(9, 30, 12, 0);

        // Act
        var duration = block.Duration;

        // Assert
        duration.Should().Be(TimeSpan.FromHours(2.5));
    }

    [Test]
    public void OwnerDate_DayShift_ReturnsStartDate()
    {
        // Arrange
        var block = CreateBlock(8, 0, 16, 0);

        // Act
        var ownerDate = block.OwnerDate;

        // Assert
        ownerDate.Should().Be(BaseDate);
    }

    [Test]
    public void OwnerDate_NightShiftOverMidnight_ReturnsStartDate()
    {
        // Arrange
        var block = CreateBlock(22, 0, 6, 0);

        // Act
        var ownerDate = block.OwnerDate;

        // Assert
        ownerDate.Should().Be(BaseDate);
    }

    [Test]
    public void TouchesDate_BlockOnlySameDay_ReturnsTrue()
    {
        // Arrange
        var block = CreateBlock(8, 0, 16, 0);

        // Act
        var touches = block.TouchesDate(BaseDate);

        // Assert
        touches.Should().BeTrue();
    }

    [Test]
    public void TouchesDate_NightShiftTouchesNextDay_ReturnsTrue()
    {
        // Arrange
        var block = CreateBlock(22, 0, 6, 0);
        var nextDay = BaseDate.AddDays(1);

        // Act
        var touches = block.TouchesDate(nextDay);

        // Assert
        touches.Should().BeTrue();
    }

    [Test]
    public void TouchesDate_BlockDoesNotTouchDay_ReturnsFalse()
    {
        // Arrange
        var block = CreateBlock(8, 0, 16, 0);
        var otherDay = BaseDate.AddDays(2);

        // Act
        var touches = block.TouchesDate(otherDay);

        // Assert
        touches.Should().BeFalse();
    }

    [Test]
    public void TouchesDate_NightShiftDoesNotTouchTwoDaysLater_ReturnsFalse()
    {
        // Arrange
        var block = CreateBlock(22, 0, 6, 0);
        var twoDaysLater = BaseDate.AddDays(2);

        // Act
        var touches = block.TouchesDate(twoDaysLater);

        // Assert
        touches.Should().BeFalse();
    }

    [Test]
    public void GetDurationOnDate_EntireBlockOnOneDay_ReturnsFullDuration()
    {
        // Arrange
        var block = CreateBlock(8, 0, 16, 0);

        // Act
        var duration = block.GetDurationOnDate(BaseDate);

        // Assert
        duration.Should().Be(TimeSpan.FromHours(8));
    }

    [Test]
    public void GetDurationOnDate_NightShiftDay1Portion_Returns2Hours()
    {
        // Arrange
        var block = CreateBlock(22, 0, 6, 0);

        // Act
        var duration = block.GetDurationOnDate(BaseDate);

        // Assert
        duration.Should().Be(TimeSpan.FromHours(2));
    }

    [Test]
    public void GetDurationOnDate_NightShiftDay2Portion_Returns6Hours()
    {
        // Arrange
        var block = CreateBlock(22, 0, 6, 0);
        var nextDay = BaseDate.AddDays(1);

        // Act
        var duration = block.GetDurationOnDate(nextDay);

        // Assert
        duration.Should().Be(TimeSpan.FromHours(6));
    }

    [Test]
    public void GetDurationOnDate_BlockDoesNotTouchDate_ReturnsZero()
    {
        // Arrange
        var block = CreateBlock(8, 0, 16, 0);
        var otherDay = BaseDate.AddDays(3);

        // Act
        var duration = block.GetDurationOnDate(otherDay);

        // Assert
        duration.Should().Be(TimeSpan.Zero);
    }

    [Test]
    public void Overlaps_TwoOverlappingBlocks_ReturnsTrue()
    {
        // Arrange
        var block1 = CreateBlock(8, 0, 14, 0);
        var block2 = CreateBlock(12, 0, 18, 0);

        // Act
        var overlaps = block1.Overlaps(block2);

        // Assert
        overlaps.Should().BeTrue();
    }

    [Test]
    public void Overlaps_NonOverlappingBlocks_ReturnsFalse()
    {
        // Arrange
        var block1 = CreateBlock(8, 0, 12, 0);
        var block2 = CreateBlock(14, 0, 18, 0);

        // Act
        var overlaps = block1.Overlaps(block2);

        // Assert
        overlaps.Should().BeFalse();
    }

    [Test]
    public void Overlaps_ExactlyAdjacentBlocks_ReturnsFalse()
    {
        // Arrange
        var block1 = CreateBlock(8, 0, 12, 0);
        var block2 = CreateBlock(12, 0, 16, 0);

        // Act
        var overlaps = block1.Overlaps(block2);

        // Assert
        overlaps.Should().BeFalse();
    }

    [Test]
    public void Overlaps_NightShiftOverlapsWithMorningShift_ReturnsTrue()
    {
        // Arrange
        var nightBlock = CreateBlock(22, 0, 6, 0);
        var nextDay = BaseDate.AddDays(1);
        var morningStart = nextDay.ToDateTime(new TimeOnly(5, 0));
        var morningEnd = nextDay.ToDateTime(new TimeOnly(13, 0));
        var morningBlock = new ScheduleBlock(Guid.NewGuid(), ScheduleBlockType.Work, ClientId, morningStart, morningEnd);

        // Act
        var overlaps = nightBlock.Overlaps(morningBlock);

        // Assert
        overlaps.Should().BeTrue();
    }

    [Test]
    public void OverlapDuration_TwoHourOverlap_Returns2Hours()
    {
        // Arrange
        var block1 = CreateBlock(8, 0, 14, 0);
        var block2 = CreateBlock(12, 0, 18, 0);

        // Act
        var overlapDuration = block1.OverlapDuration(block2);

        // Assert
        overlapDuration.Should().Be(TimeSpan.FromHours(2));
    }

    [Test]
    public void OverlapDuration_NoOverlap_ReturnsZero()
    {
        // Arrange
        var block1 = CreateBlock(8, 0, 12, 0);
        var block2 = CreateBlock(14, 0, 18, 0);

        // Act
        var overlapDuration = block1.OverlapDuration(block2);

        // Assert
        overlapDuration.Should().Be(TimeSpan.Zero);
    }

    [Test]
    public void GapTo_GapBetweenTwoBlocks_ReturnsGapDuration()
    {
        // Arrange
        var block1 = CreateBlock(8, 0, 12, 0);
        var block2 = CreateBlock(14, 0, 18, 0);

        // Act
        var gap = block1.GapTo(block2);

        // Assert
        gap.Should().Be(TimeSpan.FromHours(2));
    }

    [Test]
    public void GapTo_OverlappingBlocks_ReturnsZero()
    {
        // Arrange
        var block1 = CreateBlock(8, 0, 14, 0);
        var block2 = CreateBlock(12, 0, 18, 0);

        // Act
        var gap = block1.GapTo(block2);

        // Assert
        gap.Should().Be(TimeSpan.Zero);
    }

    [Test]
    public void GapTo_AdjacentBlocks_ReturnsZero()
    {
        // Arrange
        var block1 = CreateBlock(8, 0, 12, 0);
        var block2 = CreateBlock(12, 0, 16, 0);

        // Act
        var gap = block1.GapTo(block2);

        // Assert
        gap.Should().Be(TimeSpan.Zero);
    }

    [TestCase(14, 0, 22, 0, 8)]
    [TestCase(0, 0, 8, 0, 8)]
    [TestCase(6, 0, 14, 30, 8.5)]
    public void Duration_VariousShiftLengths_ReturnsExpected(int startH, int startM, int endH, int endM, double expectedHours)
    {
        // Arrange
        var start = BaseDate.ToDateTime(new TimeOnly(startH, startM));
        var end = BaseDate.ToDateTime(new TimeOnly(endH, endM));
        var block = new ScheduleBlock(Guid.NewGuid(), ScheduleBlockType.Work, ClientId, start, end);

        // Act
        var duration = block.Duration;

        // Assert
        duration.Should().Be(TimeSpan.FromHours(expectedHours));
    }
}
