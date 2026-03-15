using Klacks.Api.Domain.Models.Schedules;

namespace Klacks.UnitTest.Domain.Models.Schedules;

[TestFixture]
public class ScheduleBoardTests
{
    private static readonly DateOnly BaseDate = new(2026, 3, 15);
    private ScheduleBoard _board = null!;

    [SetUp]
    public void Setup()
    {
        _board = new ScheduleBoard();
    }

    private ScheduleBlock CreateWorkBlock(Guid clientId, DateTime start, DateTime end, Guid? sourceId = null)
    {
        return new ScheduleBlock(
            sourceId ?? Guid.NewGuid(), ScheduleBlockType.Work, clientId, start, end);
    }

    private void AddClientWithShift(Guid clientId, int startHour, int endHour, DateOnly? date = null)
    {
        var d = date ?? BaseDate;
        var timeline = _board.GetOrCreateTimeline(clientId);
        timeline.AddBlock(CreateWorkBlock(
            clientId,
            d.ToDateTime(new TimeOnly(startHour, 0)),
            d.ToDateTime(new TimeOnly(endHour, 0))));
        timeline.SortBlocks();
    }

    [Test]
    public void GetAllCollisions_FromMultipleTimelines_ReturnsAll()
    {
        // Arrange
        var client1 = Guid.NewGuid();
        var timeline1 = _board.GetOrCreateTimeline(client1);
        var block1 = CreateWorkBlock(client1,
            BaseDate.ToDateTime(new TimeOnly(8, 0)),
            BaseDate.ToDateTime(new TimeOnly(14, 0)));
        var block2 = CreateWorkBlock(client1,
            BaseDate.ToDateTime(new TimeOnly(12, 0)),
            BaseDate.ToDateTime(new TimeOnly(18, 0)));
        timeline1.AddBlocks([block1, block2]);
        timeline1.SortBlocks();

        var client2 = Guid.NewGuid();
        var timeline2 = _board.GetOrCreateTimeline(client2);
        var block3 = CreateWorkBlock(client2,
            BaseDate.ToDateTime(new TimeOnly(6, 0)),
            BaseDate.ToDateTime(new TimeOnly(15, 0)));
        var block4 = CreateWorkBlock(client2,
            BaseDate.ToDateTime(new TimeOnly(14, 0)),
            BaseDate.ToDateTime(new TimeOnly(22, 0)));
        timeline2.AddBlocks([block3, block4]);
        timeline2.SortBlocks();

        // Act
        var collisions = _board.GetAllCollisions();

        // Assert
        collisions.Should().HaveCount(2);
    }

    [Test]
    public void GetAllCollisions_NoCollisions_ReturnsEmpty()
    {
        // Arrange
        var client1 = Guid.NewGuid();
        AddClientWithShift(client1, 8, 12);
        var client2 = Guid.NewGuid();
        AddClientWithShift(client2, 14, 18);

        // Act
        var collisions = _board.GetAllCollisions();

        // Assert
        collisions.Should().BeEmpty();
    }

    [Test]
    public void GetStaffCount_NoOneWorking_ReturnsZero()
    {
        // Arrange
        var client1 = Guid.NewGuid();
        AddClientWithShift(client1, 8, 12);

        // Act
        var count = _board.GetStaffCount(BaseDate.ToDateTime(new TimeOnly(14, 0)));

        // Assert
        count.Should().Be(0);
    }

    [Test]
    public void GetStaffCount_OneWorking_ReturnsOne()
    {
        // Arrange
        var client1 = Guid.NewGuid();
        AddClientWithShift(client1, 8, 16);

        // Act
        var count = _board.GetStaffCount(BaseDate.ToDateTime(new TimeOnly(12, 0)));

        // Assert
        count.Should().Be(1);
    }

    [Test]
    public void GetStaffCount_ThreeWorkingSimultaneously_ReturnsThree()
    {
        // Arrange
        var client1 = Guid.NewGuid();
        var client2 = Guid.NewGuid();
        var client3 = Guid.NewGuid();
        AddClientWithShift(client1, 6, 14);
        AddClientWithShift(client2, 8, 16);
        AddClientWithShift(client3, 10, 18);

        // Act
        var count = _board.GetStaffCount(BaseDate.ToDateTime(new TimeOnly(12, 0)));

        // Assert
        count.Should().Be(3);
    }

    [Test]
    public void GetUnderstaffedPeriods_FullCoverage_ReturnsNoMeaningfulPeriods()
    {
        // Arrange
        var client1 = Guid.NewGuid();
        var client2 = Guid.NewGuid();
        AddClientWithShift(client1, 6, 14);
        AddClientWithShift(client2, 6, 14);
        var from = BaseDate.ToDateTime(new TimeOnly(6, 0));
        var to = BaseDate.ToDateTime(new TimeOnly(14, 0));

        // Act
        var periods = _board.GetUnderstaffedPeriods(from, to, minStaff: 2);

        // Assert
        var meaningfulPeriods = periods.Where(p => p.Start < p.End).ToList();
        meaningfulPeriods.Should().BeEmpty();
    }

    [Test]
    public void GetUnderstaffedPeriods_GapInCoverage_ReturnsUnderstaffedPeriod()
    {
        // Arrange
        var client1 = Guid.NewGuid();
        var client2 = Guid.NewGuid();
        AddClientWithShift(client1, 8, 12);
        AddClientWithShift(client2, 14, 18);
        var from = BaseDate.ToDateTime(new TimeOnly(8, 0));
        var to = BaseDate.ToDateTime(new TimeOnly(18, 0));

        // Act
        var periods = _board.GetUnderstaffedPeriods(from, to, minStaff: 1);

        // Assert
        var meaningfulPeriods = periods.Where(p => p.Start < p.End).ToList();
        meaningfulPeriods.Should().HaveCount(1);
        meaningfulPeriods[0].Start.Should().Be(BaseDate.ToDateTime(new TimeOnly(12, 0)));
        meaningfulPeriods[0].End.Should().Be(BaseDate.ToDateTime(new TimeOnly(14, 0)));
    }

    [Test]
    public void GetUnderstaffedPeriods_LimitApplied_StopsAtMaxResults()
    {
        // Arrange
        var client1 = Guid.NewGuid();
        var timeline = _board.GetOrCreateTimeline(client1);
        for (var h = 0; h < 20; h += 2)
        {
            timeline.AddBlock(CreateWorkBlock(client1,
                BaseDate.ToDateTime(new TimeOnly(h, 0)),
                BaseDate.ToDateTime(new TimeOnly(h + 1, 0))));
        }
        timeline.SortBlocks();
        var from = BaseDate.ToDateTime(TimeOnly.MinValue);
        var to = BaseDate.AddDays(1).ToDateTime(TimeOnly.MinValue);

        // Act
        var periods = _board.GetUnderstaffedPeriods(from, to, minStaff: 2);

        // Assert
        periods.Count.Should().BeLessThanOrEqualTo(100);
    }

    [Test]
    public void GetHourlyCoverage_ReturnsAllHours()
    {
        // Arrange
        var client1 = Guid.NewGuid();
        AddClientWithShift(client1, 8, 16);

        // Act
        var coverage = _board.GetHourlyCoverage(BaseDate);

        // Assert
        coverage.Should().HaveCount(24);
        coverage[7].Should().Be(0);
        coverage[8].Should().Be(1);
        coverage[12].Should().Be(1);
        coverage[15].Should().Be(1);
        coverage[16].Should().Be(0);
    }

    [Test]
    public void GetOvertimeViolations_Over10Hours_ReturnsViolation()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var timeline = _board.GetOrCreateTimeline(clientId);
        timeline.AddBlock(CreateWorkBlock(clientId,
            BaseDate.ToDateTime(new TimeOnly(6, 0)),
            BaseDate.ToDateTime(new TimeOnly(18, 0))));

        // Act
        var violations = _board.GetOvertimeViolations(
            BaseDate, BaseDate, TimeSpan.FromHours(10));

        // Assert
        violations.Should().HaveCount(1);
        violations[0].ClientId.Should().Be(clientId);
        violations[0].Date.Should().Be(BaseDate);
        violations[0].Duration.Should().Be(TimeSpan.FromHours(12));
    }

    [Test]
    public void GetOvertimeViolations_Exactly10Hours_ReturnsEmpty()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var timeline = _board.GetOrCreateTimeline(clientId);
        timeline.AddBlock(CreateWorkBlock(clientId,
            BaseDate.ToDateTime(new TimeOnly(6, 0)),
            BaseDate.ToDateTime(new TimeOnly(16, 0))));

        // Act
        var violations = _board.GetOvertimeViolations(
            BaseDate, BaseDate, TimeSpan.FromHours(10));

        // Assert
        violations.Should().BeEmpty();
    }

    [Test]
    public void GetConsecutiveDayViolations_SevenConsecutiveDays_ReturnsViolation()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var timeline = _board.GetOrCreateTimeline(clientId);
        for (var i = 0; i < 7; i++)
        {
            var date = BaseDate.AddDays(i);
            timeline.AddBlock(CreateWorkBlock(clientId,
                date.ToDateTime(new TimeOnly(8, 0)),
                date.ToDateTime(new TimeOnly(16, 0))));
        }

        // Act
        var violations = _board.GetConsecutiveDayViolations(
            BaseDate, BaseDate.AddDays(6), maxConsecutiveDays: 6);

        // Assert
        violations.Should().HaveCount(1);
        violations[0].ClientId.Should().Be(clientId);
        violations[0].ConsecutiveDays.Should().Be(7);
    }

    [Test]
    public void GetConsecutiveDayViolations_SixDays_ReturnsEmpty()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var timeline = _board.GetOrCreateTimeline(clientId);
        for (var i = 0; i < 6; i++)
        {
            var date = BaseDate.AddDays(i);
            timeline.AddBlock(CreateWorkBlock(clientId,
                date.ToDateTime(new TimeOnly(8, 0)),
                date.ToDateTime(new TimeOnly(16, 0))));
        }

        // Act
        var violations = _board.GetConsecutiveDayViolations(
            BaseDate, BaseDate.AddDays(5), maxConsecutiveDays: 6);

        // Assert
        violations.Should().BeEmpty();
    }

    [Test]
    public void GetAllRestViolations_CollectsFromAllTimelines()
    {
        // Arrange
        var client1 = Guid.NewGuid();
        var timeline1 = _board.GetOrCreateTimeline(client1);
        timeline1.AddBlock(CreateWorkBlock(client1,
            BaseDate.ToDateTime(new TimeOnly(6, 0)),
            BaseDate.ToDateTime(new TimeOnly(14, 0))));
        timeline1.AddBlock(CreateWorkBlock(client1,
            BaseDate.ToDateTime(new TimeOnly(20, 0)),
            BaseDate.AddDays(1).ToDateTime(new TimeOnly(4, 0))));
        timeline1.SortBlocks();

        var client2 = Guid.NewGuid();
        var timeline2 = _board.GetOrCreateTimeline(client2);
        timeline2.AddBlock(CreateWorkBlock(client2,
            BaseDate.ToDateTime(new TimeOnly(6, 0)),
            BaseDate.ToDateTime(new TimeOnly(14, 0))));
        timeline2.AddBlock(CreateWorkBlock(client2,
            BaseDate.ToDateTime(new TimeOnly(18, 0)),
            BaseDate.AddDays(1).ToDateTime(new TimeOnly(2, 0))));
        timeline2.SortBlocks();

        // Act
        var violations = _board.GetAllRestViolations(TimeSpan.FromHours(11));

        // Assert
        violations.Should().HaveCount(2);
    }

    [Test]
    public void GetOrCreateTimeline_SameClientTwice_ReturnsSameInstance()
    {
        // Arrange
        var clientId = Guid.NewGuid();

        // Act
        var timeline1 = _board.GetOrCreateTimeline(clientId);
        var timeline2 = _board.GetOrCreateTimeline(clientId);

        // Assert
        timeline1.Should().BeSameAs(timeline2);
    }

    [Test]
    public void SortAllTimelines_SortsBlocksInAllTimelines()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var timeline = _board.GetOrCreateTimeline(clientId);
        var block2 = CreateWorkBlock(clientId,
            BaseDate.ToDateTime(new TimeOnly(14, 0)),
            BaseDate.ToDateTime(new TimeOnly(18, 0)));
        var block1 = CreateWorkBlock(clientId,
            BaseDate.ToDateTime(new TimeOnly(8, 0)),
            BaseDate.ToDateTime(new TimeOnly(12, 0)));
        timeline.AddBlocks([block2, block1]);

        // Act
        _board.SortAllTimelines();

        // Assert
        timeline.Blocks[0].Start.Should().BeBefore(timeline.Blocks[1].Start);
    }
}
