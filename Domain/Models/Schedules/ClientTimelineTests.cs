using Klacks.Api.Domain.Models.Schedules;

namespace Klacks.UnitTest.Domain.Models.Schedules;

[TestFixture]
public class ClientTimelineTests
{
    private static readonly DateOnly BaseDate = new(2026, 3, 15);
    private Guid _clientId;
    private ClientTimeline _timeline = null!;

    [SetUp]
    public void Setup()
    {
        _clientId = Guid.NewGuid();
        _timeline = new ClientTimeline(_clientId);
    }

    private ScheduleBlock CreateWorkBlock(DateTime start, DateTime end, Guid? sourceId = null)
    {
        return new ScheduleBlock(
            sourceId ?? Guid.NewGuid(), ScheduleBlockType.Work, _clientId, start, end);
    }

    private ScheduleBlock CreateBreakBlock(DateTime start, DateTime end)
    {
        return new ScheduleBlock(
            Guid.NewGuid(), ScheduleBlockType.Break, _clientId, start, end);
    }

    [Test]
    public void GetCollisions_NoOverlappingBlocks_ReturnsEmpty()
    {
        // Arrange
        var block1 = CreateWorkBlock(
            BaseDate.ToDateTime(new TimeOnly(8, 0)),
            BaseDate.ToDateTime(new TimeOnly(12, 0)));
        var block2 = CreateWorkBlock(
            BaseDate.ToDateTime(new TimeOnly(14, 0)),
            BaseDate.ToDateTime(new TimeOnly(18, 0)));
        _timeline.AddBlocks([block1, block2]);
        _timeline.SortBlocks();

        // Act
        var collisions = _timeline.GetCollisions();

        // Assert
        collisions.Should().BeEmpty();
    }

    [Test]
    public void GetCollisions_OneOverlap_ReturnsOnePair()
    {
        // Arrange
        var block1 = CreateWorkBlock(
            BaseDate.ToDateTime(new TimeOnly(8, 0)),
            BaseDate.ToDateTime(new TimeOnly(14, 0)));
        var block2 = CreateWorkBlock(
            BaseDate.ToDateTime(new TimeOnly(12, 0)),
            BaseDate.ToDateTime(new TimeOnly(18, 0)));
        _timeline.AddBlocks([block1, block2]);
        _timeline.SortBlocks();

        // Act
        var collisions = _timeline.GetCollisions();

        // Assert
        collisions.Should().HaveCount(1);
        collisions[0].A.Should().Be(block1);
        collisions[0].B.Should().Be(block2);
    }

    [Test]
    public void GetCollisions_MultipleOverlaps_ReturnsAllPairs()
    {
        // Arrange
        var block1 = CreateWorkBlock(
            BaseDate.ToDateTime(new TimeOnly(8, 0)),
            BaseDate.ToDateTime(new TimeOnly(16, 0)));
        var block2 = CreateWorkBlock(
            BaseDate.ToDateTime(new TimeOnly(10, 0)),
            BaseDate.ToDateTime(new TimeOnly(14, 0)));
        var block3 = CreateWorkBlock(
            BaseDate.ToDateTime(new TimeOnly(12, 0)),
            BaseDate.ToDateTime(new TimeOnly(18, 0)));
        _timeline.AddBlocks([block1, block2, block3]);
        _timeline.SortBlocks();

        // Act
        var collisions = _timeline.GetCollisions();

        // Assert
        collisions.Should().HaveCount(3);
    }

    [Test]
    public void GetCollisions_SameSourceId_IsIgnored()
    {
        // Arrange
        var sourceId = Guid.NewGuid();
        var block1 = CreateWorkBlock(
            BaseDate.ToDateTime(new TimeOnly(8, 0)),
            BaseDate.ToDateTime(new TimeOnly(14, 0)),
            sourceId);
        var block2 = new ScheduleBlock(
            sourceId, ScheduleBlockType.Correction, _clientId,
            BaseDate.ToDateTime(new TimeOnly(12, 0)),
            BaseDate.ToDateTime(new TimeOnly(16, 0)));
        _timeline.AddBlocks([block1, block2]);
        _timeline.SortBlocks();

        // Act
        var collisions = _timeline.GetCollisions();

        // Assert
        collisions.Should().BeEmpty();
    }

    [Test]
    public void GetRestViolations_SufficientRest_ReturnsEmpty()
    {
        // Arrange
        var block1 = CreateWorkBlock(
            BaseDate.ToDateTime(new TimeOnly(6, 0)),
            BaseDate.ToDateTime(new TimeOnly(14, 0)));
        var nextDay = BaseDate.AddDays(1);
        var block2 = CreateWorkBlock(
            nextDay.ToDateTime(new TimeOnly(6, 0)),
            nextDay.ToDateTime(new TimeOnly(14, 0)));
        _timeline.AddBlocks([block1, block2]);
        _timeline.SortBlocks();

        // Act
        var violations = _timeline.GetRestViolations(TimeSpan.FromHours(11));

        // Assert
        violations.Should().BeEmpty();
    }

    [Test]
    public void GetRestViolations_InsufficientRest_ReturnsViolation()
    {
        // Arrange
        var block1 = CreateWorkBlock(
            BaseDate.ToDateTime(new TimeOnly(6, 0)),
            BaseDate.ToDateTime(new TimeOnly(14, 0)));
        var block2 = CreateWorkBlock(
            BaseDate.ToDateTime(new TimeOnly(22, 0)),
            BaseDate.AddDays(1).ToDateTime(new TimeOnly(6, 0)));
        _timeline.AddBlocks([block1, block2]);
        _timeline.SortBlocks();

        // Act
        var violations = _timeline.GetRestViolations(TimeSpan.FromHours(11));

        // Assert
        violations.Should().HaveCount(1);
        violations[0].ActualRest.Should().Be(TimeSpan.FromHours(8));
        violations[0].RequiredRest.Should().Be(TimeSpan.FromHours(11));
    }

    [Test]
    public void GetRestViolations_NightShiftToEarlyMorning_ReturnsViolation()
    {
        // Arrange
        var block1 = CreateWorkBlock(
            BaseDate.ToDateTime(new TimeOnly(22, 0)),
            BaseDate.AddDays(1).ToDateTime(new TimeOnly(6, 0)));
        var nextDay = BaseDate.AddDays(1);
        var block2 = CreateWorkBlock(
            nextDay.ToDateTime(new TimeOnly(14, 0)),
            nextDay.ToDateTime(new TimeOnly(22, 0)));
        _timeline.AddBlocks([block1, block2]);
        _timeline.SortBlocks();

        // Act
        var violations = _timeline.GetRestViolations(TimeSpan.FromHours(11));

        // Assert
        violations.Should().HaveCount(1);
        violations[0].ActualRest.Should().Be(TimeSpan.FromHours(8));
    }

    [Test]
    public void GetRestViolations_BreakBlocksIgnored_NoViolation()
    {
        // Arrange
        var workBlock = CreateWorkBlock(
            BaseDate.ToDateTime(new TimeOnly(6, 0)),
            BaseDate.ToDateTime(new TimeOnly(14, 0)));
        var breakBlock = CreateBreakBlock(
            BaseDate.ToDateTime(new TimeOnly(16, 0)),
            BaseDate.ToDateTime(new TimeOnly(20, 0)));
        _timeline.AddBlocks([workBlock, breakBlock]);
        _timeline.SortBlocks();

        // Act
        var violations = _timeline.GetRestViolations(TimeSpan.FromHours(11));

        // Assert
        violations.Should().BeEmpty();
    }

    [Test]
    public void GetWorkDuration_SingleShiftOnDate_ReturnsFullDuration()
    {
        // Arrange
        var block = CreateWorkBlock(
            BaseDate.ToDateTime(new TimeOnly(8, 0)),
            BaseDate.ToDateTime(new TimeOnly(16, 0)));
        _timeline.AddBlock(block);

        // Act
        var duration = _timeline.GetWorkDuration(BaseDate);

        // Assert
        duration.Should().Be(TimeSpan.FromHours(8));
    }

    [Test]
    public void GetWorkDuration_NightShiftProportional_ReturnsPartialDuration()
    {
        // Arrange
        var block = CreateWorkBlock(
            BaseDate.ToDateTime(new TimeOnly(22, 0)),
            BaseDate.AddDays(1).ToDateTime(new TimeOnly(6, 0)));
        _timeline.AddBlock(block);

        // Act
        var durationDay1 = _timeline.GetWorkDuration(BaseDate);
        var durationDay2 = _timeline.GetWorkDuration(BaseDate.AddDays(1));

        // Assert
        durationDay1.Should().Be(TimeSpan.FromHours(2));
        durationDay2.Should().Be(TimeSpan.FromHours(6));
    }

    [Test]
    public void GetWorkDuration_TwoShiftsSameDay_ReturnsCombined()
    {
        // Arrange
        var block1 = CreateWorkBlock(
            BaseDate.ToDateTime(new TimeOnly(6, 0)),
            BaseDate.ToDateTime(new TimeOnly(10, 0)));
        var block2 = CreateWorkBlock(
            BaseDate.ToDateTime(new TimeOnly(14, 0)),
            BaseDate.ToDateTime(new TimeOnly(18, 0)));
        _timeline.AddBlocks([block1, block2]);

        // Act
        var duration = _timeline.GetWorkDuration(BaseDate);

        // Assert
        duration.Should().Be(TimeSpan.FromHours(8));
    }

    [Test]
    public void GetWorkDuration_BreakBlockExcluded_ReturnsOnlyWorkDuration()
    {
        // Arrange
        var workBlock = CreateWorkBlock(
            BaseDate.ToDateTime(new TimeOnly(8, 0)),
            BaseDate.ToDateTime(new TimeOnly(16, 0)));
        var breakBlock = CreateBreakBlock(
            BaseDate.ToDateTime(new TimeOnly(12, 0)),
            BaseDate.ToDateTime(new TimeOnly(13, 0)));
        _timeline.AddBlocks([workBlock, breakBlock]);

        // Act
        var duration = _timeline.GetWorkDuration(BaseDate);

        // Assert
        duration.Should().Be(TimeSpan.FromHours(8));
    }

    [Test]
    public void GetConsecutiveWorkDays_NoWork_ReturnsZero()
    {
        // Arrange (empty timeline)

        // Act
        var count = _timeline.GetConsecutiveWorkDays(BaseDate);

        // Assert
        count.Should().Be(0);
    }

    [Test]
    public void GetConsecutiveWorkDays_ThreeConsecutiveDays_Returns3()
    {
        // Arrange
        for (var i = 0; i < 3; i++)
        {
            var date = BaseDate.AddDays(i);
            _timeline.AddBlock(CreateWorkBlock(
                date.ToDateTime(new TimeOnly(8, 0)),
                date.ToDateTime(new TimeOnly(16, 0))));
        }

        // Act
        var count = _timeline.GetConsecutiveWorkDays(BaseDate);

        // Assert
        count.Should().Be(3);
    }

    [Test]
    public void GetConsecutiveWorkDays_SevenConsecutiveDays_Returns7()
    {
        // Arrange
        for (var i = 0; i < 7; i++)
        {
            var date = BaseDate.AddDays(i);
            _timeline.AddBlock(CreateWorkBlock(
                date.ToDateTime(new TimeOnly(8, 0)),
                date.ToDateTime(new TimeOnly(16, 0))));
        }

        // Act
        var count = _timeline.GetConsecutiveWorkDays(BaseDate);

        // Assert
        count.Should().Be(7);
    }

    [Test]
    public void GetBlocksForDate_IncludesNightShiftFromPreviousDay()
    {
        // Arrange
        var previousDay = BaseDate.AddDays(-1);
        var nightBlock = CreateWorkBlock(
            previousDay.ToDateTime(new TimeOnly(22, 0)),
            BaseDate.ToDateTime(new TimeOnly(6, 0)));
        var dayBlock = CreateWorkBlock(
            BaseDate.ToDateTime(new TimeOnly(8, 0)),
            BaseDate.ToDateTime(new TimeOnly(16, 0)));
        _timeline.AddBlocks([nightBlock, dayBlock]);

        // Act
        var blocks = _timeline.GetBlocksForDate(BaseDate);

        // Assert
        blocks.Should().HaveCount(2);
        blocks.Should().Contain(nightBlock);
        blocks.Should().Contain(dayBlock);
    }

    [Test]
    public void GetBlocksForDate_ExcludesBlocksOnOtherDays()
    {
        // Arrange
        var otherDay = BaseDate.AddDays(2);
        var block = CreateWorkBlock(
            otherDay.ToDateTime(new TimeOnly(8, 0)),
            otherDay.ToDateTime(new TimeOnly(16, 0)));
        _timeline.AddBlock(block);

        // Act
        var blocks = _timeline.GetBlocksForDate(BaseDate);

        // Assert
        blocks.Should().BeEmpty();
    }

    [Test]
    public void IsWorking_PointInsideWorkBlock_ReturnsTrue()
    {
        // Arrange
        var block = CreateWorkBlock(
            BaseDate.ToDateTime(new TimeOnly(8, 0)),
            BaseDate.ToDateTime(new TimeOnly(16, 0)));
        _timeline.AddBlock(block);

        // Act
        var isWorking = _timeline.IsWorking(BaseDate.ToDateTime(new TimeOnly(12, 0)));

        // Assert
        isWorking.Should().BeTrue();
    }

    [Test]
    public void IsWorking_PointOutsideWorkBlock_ReturnsFalse()
    {
        // Arrange
        var block = CreateWorkBlock(
            BaseDate.ToDateTime(new TimeOnly(8, 0)),
            BaseDate.ToDateTime(new TimeOnly(16, 0)));
        _timeline.AddBlock(block);

        // Act
        var isWorking = _timeline.IsWorking(BaseDate.ToDateTime(new TimeOnly(18, 0)));

        // Assert
        isWorking.Should().BeFalse();
    }

    [Test]
    public void GetConsecutiveWorkDaysBackward_ThreeDays_Returns3()
    {
        // Arrange
        for (var i = -2; i <= 0; i++)
        {
            var date = BaseDate.AddDays(i);
            _timeline.AddBlock(CreateWorkBlock(
                date.ToDateTime(new TimeOnly(8, 0)),
                date.ToDateTime(new TimeOnly(16, 0))));
        }

        // Act
        var count = _timeline.GetConsecutiveWorkDaysBackward(BaseDate);

        // Assert
        count.Should().Be(3);
    }
}
