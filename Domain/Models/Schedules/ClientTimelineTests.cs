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
        collisions.ShouldBeEmpty();
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
        collisions.Count().ShouldBe(1);
        collisions[0].A.ShouldBe(block1);
        collisions[0].B.ShouldBe(block2);
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
        collisions.Count().ShouldBe(3);
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
        collisions.ShouldBeEmpty();
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
        violations.ShouldBeEmpty();
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
        violations.Count().ShouldBe(1);
        violations[0].ActualRest.ShouldBe(TimeSpan.FromHours(8));
        violations[0].RequiredRest.ShouldBe(TimeSpan.FromHours(11));
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
        violations.Count().ShouldBe(1);
        violations[0].ActualRest.ShouldBe(TimeSpan.FromHours(8));
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
        violations.ShouldBeEmpty();
    }

    [Test]
    public void GetRestViolations_SplitShiftSameDay_ReturnsEmpty()
    {
        // Arrange
        AddWork(BaseDate, 7, 0, BaseDate, 11, 0);
        AddWork(BaseDate, 16, 0, BaseDate, 20, 0);

        // Act
        var violations = _timeline.GetRestViolations(TimeSpan.FromHours(11));

        // Assert
        violations.ShouldBeEmpty();
    }

    [Test]
    public void GetRestViolations_SplitShiftThenNextDayTooEarly_MeasuresFromEndOfLastPart()
    {
        // Arrange
        var nextDay = BaseDate.AddDays(1);
        AddWork(BaseDate, 7, 0, BaseDate, 11, 0);
        var lastPart = AddWork(BaseDate, 16, 0, BaseDate, 20, 0);
        var nextStart = AddWork(nextDay, 5, 0, nextDay, 9, 0);

        // Act
        var violations = _timeline.GetRestViolations(TimeSpan.FromHours(11));

        // Assert
        var violation = violations.ShouldHaveSingleItem();
        violation.PreviousBlock.ShouldBe(lastPart);
        violation.NextBlock.ShouldBe(nextStart);
        violation.ActualRest.ShouldBe(TimeSpan.FromHours(9));
    }

    [Test]
    public void GetRestViolations_SplitShiftSpanExactlyDailyFrame_ReturnsEmpty()
    {
        // Arrange
        AddWork(BaseDate, 6, 0, BaseDate, 10, 0);
        AddWork(BaseDate, 15, 0, BaseDate, 19, 0);

        // Act
        var violations = _timeline.GetRestViolations(TimeSpan.FromHours(11));

        // Assert
        violations.ShouldBeEmpty();
    }

    [Test]
    public void GetRestViolations_SplitShiftSpanBeyondDailyFrame_ReturnsViolation()
    {
        // Arrange
        AddWork(BaseDate, 6, 0, BaseDate, 10, 0);
        AddWork(BaseDate, 15, 0, BaseDate, 19, 1);

        // Act
        var violations = _timeline.GetRestViolations(TimeSpan.FromHours(11));

        // Assert
        violations.ShouldHaveSingleItem().ActualRest.ShouldBe(TimeSpan.FromHours(5));
    }

    [Test]
    public void GetRestViolations_NightShiftThenEarlyShiftNextMorning_ReturnsViolation()
    {
        // Arrange
        var nextDay = BaseDate.AddDays(1);
        AddWork(BaseDate, 22, 0, nextDay, 6, 0);
        AddWork(nextDay, 10, 0, nextDay, 14, 0);

        // Act
        var violations = _timeline.GetRestViolations(TimeSpan.FromHours(11));

        // Assert
        violations.ShouldHaveSingleItem().ActualRest.ShouldBe(TimeSpan.FromHours(4));
    }

    [Test]
    public void GetRestViolations_ContainedBlock_MeasuresFromLatestEnd()
    {
        // Arrange
        var nextDay = BaseDate.AddDays(1);
        var longBlock = AddWork(BaseDate, 8, 0, BaseDate, 20, 0);
        AddWork(BaseDate, 9, 0, BaseDate, 10, 0);
        AddWork(nextDay, 6, 0, nextDay, 14, 0);

        // Act
        var violations = _timeline.GetRestViolations(TimeSpan.FromHours(11));

        // Assert
        var violation = violations.ShouldHaveSingleItem();
        violation.PreviousBlock.ShouldBe(longBlock);
        violation.ActualRest.ShouldBe(TimeSpan.FromHours(10));
    }

    [Test]
    public void GetRestGaps_SplitShiftDays_ReturnsOnlyGapsBetweenWorkDays()
    {
        // Arrange
        var nextDay = BaseDate.AddDays(1);
        AddWork(BaseDate, 7, 0, BaseDate, 11, 0);
        AddWork(BaseDate, 16, 0, BaseDate, 20, 0);
        AddWork(nextDay, 7, 0, nextDay, 11, 0);
        AddWork(nextDay, 16, 0, nextDay, 20, 0);

        // Act
        var gaps = _timeline.GetRestGaps(TimeSpan.FromHours(11));

        // Assert
        var gap = gaps.ShouldHaveSingleItem();
        gap.PreviousBlock.End.ShouldBe(BaseDate.ToDateTime(new TimeOnly(20, 0)));
        gap.NextBlock.Start.ShouldBe(nextDay.ToDateTime(new TimeOnly(7, 0)));
        gap.Duration.ShouldBe(TimeSpan.FromHours(11));
    }

    private ScheduleBlock AddWork(DateOnly startDate, int startHour, int startMinute, DateOnly endDate, int endHour, int endMinute)
    {
        var block = CreateWorkBlock(
            startDate.ToDateTime(new TimeOnly(startHour, startMinute)),
            endDate.ToDateTime(new TimeOnly(endHour, endMinute)));
        _timeline.AddBlock(block);
        return block;
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
        duration.ShouldBe(TimeSpan.FromHours(8));
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
        durationDay1.ShouldBe(TimeSpan.FromHours(2));
        durationDay2.ShouldBe(TimeSpan.FromHours(6));
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
        duration.ShouldBe(TimeSpan.FromHours(8));
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
        duration.ShouldBe(TimeSpan.FromHours(8));
    }

    [Test]
    public void GetConsecutiveWorkDays_NoWork_ReturnsZero()
    {
        // Arrange (empty timeline)

        // Act
        var count = _timeline.GetConsecutiveWorkDays(BaseDate);

        // Assert
        count.ShouldBe(0);
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
        count.ShouldBe(3);
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
        count.ShouldBe(7);
    }

    [Test]
    public void GetConsecutiveWorkDays_NightShiftRun_CountsAnchorsOnly()
    {
        // Arrange: 3 consecutive night shifts Mon 23:00 → Tue 07:00, Tue 23:00 → Wed 07:00,
        // Wed 23:00 → Thu 07:00. Each shift's anchor is its start day (Mon/Tue/Wed).
        // "Consecutive work days" counts started shifts — the Thu morning spillover from Wed's
        // night shift is the tail of an already-counted shift, not a separately started workday.
        for (var i = 0; i < 3; i++)
        {
            var date = BaseDate.AddDays(i);
            _timeline.AddBlock(CreateWorkBlock(
                date.ToDateTime(new TimeOnly(23, 0)),
                date.AddDays(1).ToDateTime(new TimeOnly(7, 0))));
        }

        // Act
        var count = _timeline.GetConsecutiveWorkDays(BaseDate);

        // Assert: 3 anchors (Mon, Tue, Wed). Thu has only spillover from Wed and no new anchor.
        count.ShouldBe(3);
    }

    [Test]
    public void GetConsecutiveWorkDays_SingleNightShift_CountsOneAnchor()
    {
        // Arrange: single night shift Mon 23:00 → Tue 07:00.
        // Mon is the anchor; Tue has only Mon's spillover but no own started shift.
        _timeline.AddBlock(CreateWorkBlock(
            BaseDate.ToDateTime(new TimeOnly(23, 0)),
            BaseDate.AddDays(1).ToDateTime(new TimeOnly(7, 0))));

        // Act
        var count = _timeline.GetConsecutiveWorkDays(BaseDate);

        // Assert: 1 anchor (Mon). Tue's spillover is not a separately started workday.
        count.ShouldBe(1);
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
        blocks.Count().ShouldBe(2);
        blocks.ShouldContain(nightBlock);
        blocks.ShouldContain(dayBlock);
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
        blocks.ShouldBeEmpty();
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
        isWorking.ShouldBeTrue();
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
        isWorking.ShouldBeFalse();
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
        count.ShouldBe(3);
    }

    private static readonly DateOnly MondayAnchor = new(2026, 3, 2);

    private const decimal MinimumRestDays = 2m;

    [Test]
    public void GetWeeklyWorkDuration_FiveEightHourDays_Returns40()
    {
        // Arrange
        for (var i = 0; i < 5; i++)
        {
            var date = MondayAnchor.AddDays(i);
            _timeline.AddBlock(CreateWorkBlock(
                date.ToDateTime(new TimeOnly(8, 0)),
                date.ToDateTime(new TimeOnly(16, 0))));
        }

        // Act
        var duration = _timeline.GetWeeklyWorkDuration(MondayAnchor);

        // Assert
        duration.ShouldBe(TimeSpan.FromHours(40));
    }

    [Test]
    public void GetWeeklyWorkDuration_BreakExcludedFromWeeklyTotal()
    {
        // Arrange
        _timeline.AddBlock(CreateWorkBlock(
            MondayAnchor.ToDateTime(new TimeOnly(8, 0)),
            MondayAnchor.ToDateTime(new TimeOnly(16, 0))));
        var breakDay = MondayAnchor.AddDays(1);
        _timeline.AddBlock(CreateBreakBlock(
            breakDay.ToDateTime(new TimeOnly(8, 0)),
            breakDay.ToDateTime(new TimeOnly(16, 0))));

        // Act
        var duration = _timeline.GetWeeklyWorkDuration(MondayAnchor);

        // Assert
        duration.ShouldBe(TimeSpan.FromHours(8));
    }

    [Test]
    public void GetRestDayCount_FiveWorkDays_ReturnsTwoRestDays()
    {
        // Arrange
        for (var i = 0; i < 5; i++)
        {
            var date = MondayAnchor.AddDays(i);
            _timeline.AddBlock(CreateWorkBlock(
                date.ToDateTime(new TimeOnly(8, 0)),
                date.ToDateTime(new TimeOnly(16, 0))));
        }

        // Act
        var restDays = _timeline.GetRestDayCount(MondayAnchor, MinimumRestDays);

        // Assert
        restDays.ShouldBe(2);
    }

    [Test]
    public void GetRestDayCount_SevenWorkDays_ReturnsZeroRestDays()
    {
        // Arrange
        for (var i = 0; i < 7; i++)
        {
            var date = MondayAnchor.AddDays(i);
            _timeline.AddBlock(CreateWorkBlock(
                date.ToDateTime(new TimeOnly(8, 0)),
                date.ToDateTime(new TimeOnly(16, 0))));
        }

        // Act
        var restDays = _timeline.GetRestDayCount(MondayAnchor, MinimumRestDays);

        // Assert
        restDays.ShouldBe(0);
    }

    [Test]
    public void GetRestDayCount_BreakDayCountsAsRest()
    {
        // Arrange
        for (var i = 0; i < 6; i++)
        {
            var date = MondayAnchor.AddDays(i);
            _timeline.AddBlock(CreateWorkBlock(
                date.ToDateTime(new TimeOnly(8, 0)),
                date.ToDateTime(new TimeOnly(16, 0))));
        }
        var breakDay = MondayAnchor.AddDays(6);
        _timeline.AddBlock(CreateBreakBlock(
            breakDay.ToDateTime(new TimeOnly(8, 0)),
            breakDay.ToDateTime(new TimeOnly(16, 0))));

        // Act
        var restDays = _timeline.GetRestDayCount(MondayAnchor, MinimumRestDays);

        // Assert
        restDays.ShouldBe(1);
    }

    [Test]
    public void GetRestDayCount_NightSpilloverDayWithFreeNextDayAndFullFreeBlockIsRest()
    {
        // Arrange
        _timeline.AddBlock(CreateWorkBlock(
            MondayAnchor.ToDateTime(new TimeOnly(22, 0)),
            MondayAnchor.AddDays(1).ToDateTime(new TimeOnly(6, 0))));
        for (var i = 3; i < 7; i++)
        {
            var date = MondayAnchor.AddDays(i);
            _timeline.AddBlock(CreateWorkBlock(
                date.ToDateTime(new TimeOnly(6, 0)),
                date.ToDateTime(new TimeOnly(14, 0))));
        }

        // Act
        var restDays = _timeline.GetRestDayCount(MondayAnchor, MinimumRestDays);
        var restDaysWithThreeDayFreeBlock = _timeline.GetRestDayCount(MondayAnchor, 3m);

        // Assert
        restDays.ShouldBe(2);
        restDaysWithThreeDayFreeBlock.ShouldBe(1);
    }

    [Test]
    public void GetRestDayCount_NightEndingAtMidnightDoesNotOccupyTheFollowingDay()
    {
        // Arrange
        _timeline.AddBlock(CreateWorkBlock(
            MondayAnchor.ToDateTime(new TimeOnly(16, 0)),
            MondayAnchor.AddDays(1).ToDateTime(TimeOnly.MinValue)));

        // Act
        var restDays = _timeline.GetRestDayCount(MondayAnchor, MinimumRestDays);

        // Assert
        restDays.ShouldBe(6);
    }

    [Test]
    public void GetRestDayCount_NightSpilloverDayFollowedByAShiftTheNextDayIsWork()
    {
        // Arrange
        var saturday = MondayAnchor.AddDays(5);
        _timeline.AddBlock(CreateWorkBlock(
            saturday.ToDateTime(new TimeOnly(22, 0)),
            saturday.AddDays(1).ToDateTime(new TimeOnly(6, 0))));
        _timeline.AddBlock(CreateWorkBlock(
            saturday.AddDays(2).ToDateTime(new TimeOnly(6, 0)),
            saturday.AddDays(2).ToDateTime(new TimeOnly(14, 0))));

        // Act
        var restDays = _timeline.GetRestDayCount(MondayAnchor, MinimumRestDays);

        // Assert
        restDays.ShouldBe(5);
    }
}