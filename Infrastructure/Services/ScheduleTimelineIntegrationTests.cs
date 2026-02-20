using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Services;

namespace Klacks.UnitTest.Infrastructure.Services;

[TestFixture]
public class ScheduleTimelineIntegrationTests
{
    private static readonly DateOnly TestDate = new(2025, 1, 15);

    private static Work CreateWork(
        Guid? id = null,
        Guid? clientId = null,
        DateOnly? date = null,
        TimeOnly? start = null,
        TimeOnly? end = null)
    {
        return new Work
        {
            Id = id ?? Guid.NewGuid(),
            ClientId = clientId ?? Guid.NewGuid(),
            CurrentDate = date ?? TestDate,
            StartTime = start ?? new TimeOnly(8, 0),
            EndTime = end ?? new TimeOnly(16, 0),
            ShiftId = Guid.NewGuid()
        };
    }

    private static WorkChange CreateWorkChange(
        Guid workId,
        WorkChangeType type,
        TimeOnly start,
        TimeOnly end,
        Guid? replaceClientId = null)
    {
        return new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = workId,
            Type = type,
            StartTime = start,
            EndTime = end,
            ReplaceClientId = replaceClientId
        };
    }

    private static Break CreateBreak(
        Guid? clientId = null,
        DateOnly? date = null,
        TimeOnly? start = null,
        TimeOnly? end = null)
    {
        return new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId ?? Guid.NewGuid(),
            CurrentDate = date ?? TestDate,
            StartTime = start ?? new TimeOnly(12, 0),
            EndTime = end ?? new TimeOnly(12, 30),
            AbsenceId = Guid.NewGuid()
        };
    }

    private static List<(TimeRect A, TimeRect B)> RunFullPipeline(
        List<Work> works,
        List<WorkChange> workChanges,
        List<Break> breaks,
        Guid clientId,
        DateOnly? date = null)
    {
        var d = date ?? TestDate;
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects(works, workChanges, breaks);
        var timeline = new ClientDayTimeline(clientId, d);
        timeline.Rects.AddRange(rects.Where(r => r.ClientId == clientId && r.Date == d));
        return timeline.GetCollisions();
    }

    #region Multiple Works - Same Client

    [Test]
    public void TwoWorks_SameClient_NoOverlap_NoCollision()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var work1 = CreateWork(clientId: clientId, start: new TimeOnly(6, 0), end: new TimeOnly(10, 0));
        var work2 = CreateWork(clientId: clientId, start: new TimeOnly(14, 0), end: new TimeOnly(18, 0));

        // Act
        var collisions = RunFullPipeline([work1, work2], [], [], clientId);

        // Assert
        collisions.Should().BeEmpty();
    }

    [Test]
    public void TwoWorks_SameClient_Adjacent_NoCollision()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var work1 = CreateWork(clientId: clientId, start: new TimeOnly(8, 0), end: new TimeOnly(12, 0));
        var work2 = CreateWork(clientId: clientId, start: new TimeOnly(12, 0), end: new TimeOnly(16, 0));

        // Act
        var collisions = RunFullPipeline([work1, work2], [], [], clientId);

        // Assert
        collisions.Should().BeEmpty();
    }

    [Test]
    public void TwoWorks_SameClient_Overlapping_OneCollision()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var work1 = CreateWork(clientId: clientId, start: new TimeOnly(8, 0), end: new TimeOnly(14, 0));
        var work2 = CreateWork(clientId: clientId, start: new TimeOnly(12, 0), end: new TimeOnly(18, 0));

        // Act
        var collisions = RunFullPipeline([work1, work2], [], [], clientId);

        // Assert
        collisions.Should().HaveCount(1);
    }

    [Test]
    public void TwoWorks_SameClient_OneContainsOther_OneCollision()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var work1 = CreateWork(clientId: clientId, start: new TimeOnly(6, 0), end: new TimeOnly(20, 0));
        var work2 = CreateWork(clientId: clientId, start: new TimeOnly(10, 0), end: new TimeOnly(14, 0));

        // Act
        var collisions = RunFullPipeline([work1, work2], [], [], clientId);

        // Assert
        collisions.Should().HaveCount(1);
    }

    [Test]
    public void ThreeWorks_SameClient_AllOverlapping_ThreeCollisions()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var work1 = CreateWork(clientId: clientId, start: new TimeOnly(8, 0), end: new TimeOnly(14, 0));
        var work2 = CreateWork(clientId: clientId, start: new TimeOnly(10, 0), end: new TimeOnly(16, 0));
        var work3 = CreateWork(clientId: clientId, start: new TimeOnly(12, 0), end: new TimeOnly(18, 0));

        // Act
        var collisions = RunFullPipeline([work1, work2, work3], [], [], clientId);

        // Assert
        collisions.Should().HaveCount(3);
    }

    [Test]
    public void ThreeWorks_SameClient_TwoOverlap_OneCollision()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var work1 = CreateWork(clientId: clientId, start: new TimeOnly(8, 0), end: new TimeOnly(12, 0));
        var work2 = CreateWork(clientId: clientId, start: new TimeOnly(11, 0), end: new TimeOnly(14, 0));
        var work3 = CreateWork(clientId: clientId, start: new TimeOnly(16, 0), end: new TimeOnly(20, 0));

        // Act
        var collisions = RunFullPipeline([work1, work2, work3], [], [], clientId);

        // Assert
        collisions.Should().HaveCount(1);
    }

    #endregion

    #region Multiple Works - Different Clients

    [Test]
    public void TwoWorks_DifferentClients_Overlapping_NoCollisionPerClient()
    {
        // Arrange
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        var work1 = CreateWork(clientId: clientA, start: new TimeOnly(8, 0), end: new TimeOnly(14, 0));
        var work2 = CreateWork(clientId: clientB, start: new TimeOnly(10, 0), end: new TimeOnly(16, 0));

        // Act
        var collisionsA = RunFullPipeline([work1, work2], [], [], clientA);
        var collisionsB = RunFullPipeline([work1, work2], [], [], clientB);

        // Assert
        collisionsA.Should().BeEmpty();
        collisionsB.Should().BeEmpty();
    }

    #endregion

    #region CorrectionStart + CorrectionEnd Combined

    [Test]
    public void Work_BothCorrectionStartAndEnd_ThreeRects()
    {
        // Arrange
        var work = CreateWork(start: new TimeOnly(8, 0), end: new TimeOnly(16, 0));
        var corrStart = CreateWorkChange(work.Id, WorkChangeType.CorrectionStart,
            new TimeOnly(9, 0), new TimeOnly(9, 0));
        var corrEnd = CreateWorkChange(work.Id, WorkChangeType.CorrectionEnd,
            new TimeOnly(15, 0), new TimeOnly(15, 0));

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work], [corrStart, corrEnd], []);

        // Assert
        var workRect = rects.Single(r => r.SourceType == TimeRectSourceType.Work);
        workRect.Start.Should().Be(new TimeOnly(9, 0));
        workRect.End.Should().Be(new TimeOnly(15, 0));

        var correctionRects = rects.Where(r => r.SourceType == TimeRectSourceType.Correction).ToList();
        correctionRects.Should().HaveCount(2);
        correctionRects.Should().Contain(r => r.Start == new TimeOnly(9, 0) && r.End == new TimeOnly(9, 0));
        correctionRects.Should().Contain(r => r.Start == new TimeOnly(15, 0) && r.End == new TimeOnly(15, 0));
    }

    [Test]
    public void Work_CorrectionStartWithDuration_CorrectRectTimes()
    {
        // Arrange
        var work = CreateWork(start: new TimeOnly(8, 0), end: new TimeOnly(16, 0));
        var corrStart = CreateWorkChange(work.Id, WorkChangeType.CorrectionStart,
            new TimeOnly(8, 0), new TimeOnly(10, 0));

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work], [corrStart], []);

        // Assert
        var workRect = rects.Single(r => r.SourceType == TimeRectSourceType.Work);
        workRect.Start.Should().Be(new TimeOnly(8, 0));
        workRect.End.Should().Be(new TimeOnly(16, 0));

        var corrRect = rects.Single(r => r.SourceType == TimeRectSourceType.Correction);
        corrRect.Start.Should().Be(new TimeOnly(8, 0));
        corrRect.End.Should().Be(new TimeOnly(10, 0));
    }

    [Test]
    public void Work_CorrectionEndWithDuration_CorrectRectTimes()
    {
        // Arrange
        var work = CreateWork(start: new TimeOnly(8, 0), end: new TimeOnly(16, 0));
        var corrEnd = CreateWorkChange(work.Id, WorkChangeType.CorrectionEnd,
            new TimeOnly(14, 0), new TimeOnly(16, 0));

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work], [corrEnd], []);

        // Assert
        var workRect = rects.Single(r => r.SourceType == TimeRectSourceType.Work);
        workRect.Start.Should().Be(new TimeOnly(8, 0));
        workRect.End.Should().Be(new TimeOnly(16, 0));

        var corrRect = rects.Single(r => r.SourceType == TimeRectSourceType.Correction);
        corrRect.Start.Should().Be(new TimeOnly(14, 0));
        corrRect.End.Should().Be(new TimeOnly(16, 0));
    }

    #endregion

    #region ReplacementStart + ReplacementEnd Combined

    [Test]
    public void Work_BothReplacementStartAndEnd_WorkShortenedBothSides()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var replaceClientA = Guid.NewGuid();
        var replaceClientB = Guid.NewGuid();
        var work = CreateWork(clientId: clientId, start: new TimeOnly(8, 0), end: new TimeOnly(16, 0));
        var replStart = CreateWorkChange(work.Id, WorkChangeType.ReplacementStart,
            new TimeOnly(8, 0), new TimeOnly(10, 0), replaceClientA);
        var replEnd = CreateWorkChange(work.Id, WorkChangeType.ReplacementEnd,
            new TimeOnly(14, 0), new TimeOnly(16, 0), replaceClientB);

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work], [replStart, replEnd], []);

        // Assert
        var workRect = rects.Single(r => r.SourceType == TimeRectSourceType.Work);
        workRect.Start.Should().Be(new TimeOnly(10, 0));
        workRect.End.Should().Be(new TimeOnly(14, 0));
        workRect.ClientId.Should().Be(clientId);

        var replacementRects = rects.Where(r => r.SourceType == TimeRectSourceType.Replacement).ToList();
        replacementRects.Should().HaveCount(2);

        var replRectA = replacementRects.Single(r => r.ClientId == replaceClientA);
        replRectA.Start.Should().Be(new TimeOnly(8, 0));
        replRectA.End.Should().Be(new TimeOnly(10, 0));

        var replRectB = replacementRects.Single(r => r.ClientId == replaceClientB);
        replRectB.Start.Should().Be(new TimeOnly(14, 0));
        replRectB.End.Should().Be(new TimeOnly(16, 0));
    }

    [Test]
    public void Work_ReplacementStart_SameReplaceClient_RectBelongsToReplaceClient()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var replaceClientId = Guid.NewGuid();
        var work = CreateWork(clientId: clientId, start: new TimeOnly(6, 0), end: new TimeOnly(14, 0));
        var change = CreateWorkChange(work.Id, WorkChangeType.ReplacementStart,
            new TimeOnly(6, 0), new TimeOnly(9, 0), replaceClientId);

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work], [change], []);

        // Assert
        var replRect = rects.Single(r => r.SourceType == TimeRectSourceType.Replacement);
        replRect.ClientId.Should().Be(replaceClientId);
        replRect.SourceId.Should().Be(work.Id);
    }

    #endregion

    #region Mixed Corrections + Replacements

    [Test]
    public void Work_CorrectionStartAndReplacementEnd_BothApply()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var replaceClientId = Guid.NewGuid();
        var work = CreateWork(clientId: clientId, start: new TimeOnly(8, 0), end: new TimeOnly(16, 0));
        var corrStart = CreateWorkChange(work.Id, WorkChangeType.CorrectionStart,
            new TimeOnly(9, 0), new TimeOnly(9, 0));
        var replEnd = CreateWorkChange(work.Id, WorkChangeType.ReplacementEnd,
            new TimeOnly(14, 0), new TimeOnly(16, 0), replaceClientId);

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work], [corrStart, replEnd], []);

        // Assert
        var workRect = rects.Single(r => r.SourceType == TimeRectSourceType.Work);
        workRect.Start.Should().Be(new TimeOnly(9, 0));
        workRect.End.Should().Be(new TimeOnly(14, 0));
        workRect.ClientId.Should().Be(clientId);

        var corrRect = rects.Single(r => r.SourceType == TimeRectSourceType.Correction);
        corrRect.Start.Should().Be(new TimeOnly(9, 0));
        corrRect.End.Should().Be(new TimeOnly(9, 0));
        corrRect.ClientId.Should().Be(clientId);

        var replRect = rects.Single(r => r.SourceType == TimeRectSourceType.Replacement);
        replRect.Start.Should().Be(new TimeOnly(14, 0));
        replRect.End.Should().Be(new TimeOnly(16, 0));
        replRect.ClientId.Should().Be(replaceClientId);
    }

    [Test]
    public void Work_CorrectionEndAndReplacementStart_BothApply()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var replaceClientId = Guid.NewGuid();
        var work = CreateWork(clientId: clientId, start: new TimeOnly(8, 0), end: new TimeOnly(16, 0));
        var replStart = CreateWorkChange(work.Id, WorkChangeType.ReplacementStart,
            new TimeOnly(8, 0), new TimeOnly(10, 0), replaceClientId);
        var corrEnd = CreateWorkChange(work.Id, WorkChangeType.CorrectionEnd,
            new TimeOnly(15, 0), new TimeOnly(15, 0));

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work], [replStart, corrEnd], []);

        // Assert
        var workRect = rects.Single(r => r.SourceType == TimeRectSourceType.Work);
        workRect.Start.Should().Be(new TimeOnly(10, 0));
        workRect.End.Should().Be(new TimeOnly(15, 0));

        rects.Should().Contain(r => r.SourceType == TimeRectSourceType.Correction);
        rects.Should().Contain(r => r.SourceType == TimeRectSourceType.Replacement);
    }

    [Test]
    public void Work_AllFourChangeTypes_ComplexScenario()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var replaceClientA = Guid.NewGuid();
        var replaceClientB = Guid.NewGuid();
        var work = CreateWork(clientId: clientId, start: new TimeOnly(6, 0), end: new TimeOnly(18, 0));

        var corrStart = CreateWorkChange(work.Id, WorkChangeType.CorrectionStart,
            new TimeOnly(7, 0), new TimeOnly(7, 0));
        var corrEnd = CreateWorkChange(work.Id, WorkChangeType.CorrectionEnd,
            new TimeOnly(17, 0), new TimeOnly(17, 0));
        var replStart = CreateWorkChange(work.Id, WorkChangeType.ReplacementStart,
            new TimeOnly(6, 0), new TimeOnly(9, 0), replaceClientA);
        var replEnd = CreateWorkChange(work.Id, WorkChangeType.ReplacementEnd,
            new TimeOnly(15, 0), new TimeOnly(18, 0), replaceClientB);

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects(
            [work], [corrStart, corrEnd, replStart, replEnd], []);

        // Assert
        rects.Should().HaveCount(5);
        rects.Where(r => r.SourceType == TimeRectSourceType.Work).Should().HaveCount(1);
        rects.Where(r => r.SourceType == TimeRectSourceType.Correction).Should().HaveCount(2);
        rects.Where(r => r.SourceType == TimeRectSourceType.Replacement).Should().HaveCount(2);
    }

    #endregion

    #region Overnight Scenarios

    [Test]
    public void OvernightWork_WithCorrectionStart_BothRectsAdjusted()
    {
        // Arrange
        var work = CreateWork(start: new TimeOnly(22, 0), end: new TimeOnly(6, 0));
        var corrStart = CreateWorkChange(work.Id, WorkChangeType.CorrectionStart,
            new TimeOnly(23, 0), new TimeOnly(23, 0));

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work], [corrStart], []);

        // Assert
        var workRects = rects.Where(r => r.SourceType == TimeRectSourceType.Work).ToList();
        workRects.Should().HaveCount(2);

        var dayRect = workRects.Single(r => r.Date == TestDate);
        dayRect.Start.Should().Be(new TimeOnly(23, 0));
        dayRect.End.Should().Be(TimeOnly.MaxValue);

        var nextDayRect = workRects.Single(r => r.Date == TestDate.AddDays(1));
        nextDayRect.Start.Should().Be(TimeOnly.MinValue);
        nextDayRect.End.Should().Be(new TimeOnly(6, 0));
    }

    [Test]
    public void OvernightWork_WithReplacementEnd_ReplacementAlsoSplits()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var replaceClientId = Guid.NewGuid();
        var work = CreateWork(clientId: clientId, start: new TimeOnly(22, 0), end: new TimeOnly(6, 0));
        var replEnd = CreateWorkChange(work.Id, WorkChangeType.ReplacementEnd,
            new TimeOnly(2, 0), new TimeOnly(6, 0), replaceClientId);

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work], [replEnd], []);

        // Assert
        var workRects = rects.Where(r => r.SourceType == TimeRectSourceType.Work).ToList();
        workRects.Should().HaveCount(2);

        var replRects = rects.Where(r => r.SourceType == TimeRectSourceType.Replacement).ToList();
        replRects.Should().HaveCount(1);
        replRects[0].ClientId.Should().Be(replaceClientId);
        replRects[0].Start.Should().Be(new TimeOnly(2, 0));
        replRects[0].End.Should().Be(new TimeOnly(6, 0));
    }

    [Test]
    public void TwoOvernightWorks_SameClient_CollisionOnNextDay()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var work1 = CreateWork(clientId: clientId, start: new TimeOnly(22, 0), end: new TimeOnly(4, 0));
        var work2 = CreateWork(clientId: clientId, start: new TimeOnly(23, 0), end: new TimeOnly(5, 0));

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work1, work2], [], []);

        var nextDay = TestDate.AddDays(1);
        var timelineNextDay = new ClientDayTimeline(clientId, nextDay);
        timelineNextDay.Rects.AddRange(rects.Where(r => r.ClientId == clientId && r.Date == nextDay));
        var collisionsNextDay = timelineNextDay.GetCollisions();

        // Assert
        collisionsNextDay.Should().HaveCount(1);
    }

    [Test]
    public void TwoOvernightWorks_SameClient_CollisionOnSameDay()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var work1 = CreateWork(clientId: clientId, start: new TimeOnly(20, 0), end: new TimeOnly(4, 0));
        var work2 = CreateWork(clientId: clientId, start: new TimeOnly(21, 0), end: new TimeOnly(3, 0));

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work1, work2], [], []);

        var timelineSameDay = new ClientDayTimeline(clientId, TestDate);
        timelineSameDay.Rects.AddRange(rects.Where(r => r.ClientId == clientId && r.Date == TestDate));
        var collisionsSameDay = timelineSameDay.GetCollisions();

        // Assert
        collisionsSameDay.Should().HaveCount(1);
    }

    [Test]
    public void OvernightBreak_CreatesTwoRects()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var breakEntry = CreateBreak(clientId: clientId, start: new TimeOnly(23, 0), end: new TimeOnly(1, 0));

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([], [], [breakEntry]);

        // Assert
        rects.Should().HaveCount(2);
        var dayRect = rects.Single(r => r.Date == TestDate);
        dayRect.Start.Should().Be(new TimeOnly(23, 0));
        dayRect.End.Should().Be(TimeOnly.MaxValue);

        var nextDayRect = rects.Single(r => r.Date == TestDate.AddDays(1));
        nextDayRect.Start.Should().Be(TimeOnly.MinValue);
        nextDayRect.End.Should().Be(new TimeOnly(1, 0));
    }

    #endregion

    #region Break Scenarios

    [Test]
    public void WorkAndBreak_SameClient_Overlapping_NoCollision()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var work = CreateWork(clientId: clientId, start: new TimeOnly(8, 0), end: new TimeOnly(16, 0));
        var breakEntry = CreateBreak(clientId: clientId, start: new TimeOnly(12, 0), end: new TimeOnly(12, 30));

        // Act
        var collisions = RunFullPipeline([work], [], [breakEntry], clientId);

        // Assert
        collisions.Should().BeEmpty();
    }

    [Test]
    public void MultipleBreaks_SameClient_Overlapping_NoCollision()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var break1 = CreateBreak(clientId: clientId, start: new TimeOnly(12, 0), end: new TimeOnly(12, 30));
        var break2 = CreateBreak(clientId: clientId, start: new TimeOnly(12, 15), end: new TimeOnly(12, 45));

        // Act
        var collisions = RunFullPipeline([], [], [break1, break2], clientId);

        // Assert
        collisions.Should().BeEmpty();
    }

    [Test]
    public void WorkAndBreak_BreakRectNotInCollisionDetection()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var work1 = CreateWork(clientId: clientId, start: new TimeOnly(8, 0), end: new TimeOnly(16, 0));
        var breakEntry = CreateBreak(clientId: clientId, start: new TimeOnly(8, 0), end: new TimeOnly(16, 0));

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work1], [], [breakEntry]);
        var timeline = new ClientDayTimeline(clientId, TestDate);
        timeline.Rects.AddRange(rects.Where(r => r.ClientId == clientId && r.Date == TestDate));
        var collisions = timeline.GetCollisions();

        // Assert
        rects.Should().HaveCount(2);
        collisions.Should().BeEmpty();
    }

    #endregion

    #region End-to-End Collision: CalculateTimeRects → GetCollisions

    [Test]
    public void E2E_ReplacementCreatesCollisionWithOtherWork()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var replaceClientId = Guid.NewGuid();
        var work1 = CreateWork(clientId: clientId, start: new TimeOnly(8, 0), end: new TimeOnly(16, 0));
        var work2 = CreateWork(clientId: replaceClientId, start: new TimeOnly(10, 0), end: new TimeOnly(14, 0));
        var replStart = CreateWorkChange(work1.Id, WorkChangeType.ReplacementStart,
            new TimeOnly(8, 0), new TimeOnly(12, 0), replaceClientId);

        // Act
        var collisions = RunFullPipeline([work1, work2], [replStart], [], replaceClientId);

        // Assert
        collisions.Should().HaveCount(1);
    }

    [Test]
    public void E2E_CorrectionDoesNotCollideWithSameSourceId()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var work = CreateWork(clientId: clientId, start: new TimeOnly(8, 0), end: new TimeOnly(16, 0));
        var corrStart = CreateWorkChange(work.Id, WorkChangeType.CorrectionStart,
            new TimeOnly(9, 0), new TimeOnly(9, 0));

        // Act
        var collisions = RunFullPipeline([work], [corrStart], [], clientId);

        // Assert
        collisions.Should().BeEmpty();
    }

    [Test]
    public void E2E_CorrectionCollidesWithDifferentWork()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var work1 = CreateWork(clientId: clientId, start: new TimeOnly(8, 0), end: new TimeOnly(12, 0));
        var work2 = CreateWork(clientId: clientId, start: new TimeOnly(14, 0), end: new TimeOnly(18, 0));
        var corrEnd = CreateWorkChange(work1.Id, WorkChangeType.CorrectionEnd,
            new TimeOnly(12, 0), new TimeOnly(16, 0));

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work1, work2], [corrEnd], []);
        var timeline = new ClientDayTimeline(clientId, TestDate);
        timeline.Rects.AddRange(rects.Where(r => r.ClientId == clientId && r.Date == TestDate));
        var collisions = timeline.GetCollisions();

        // Assert
        var corrRect = rects.Single(r => r.SourceType == TimeRectSourceType.Correction);
        corrRect.Start.Should().Be(new TimeOnly(12, 0));
        corrRect.End.Should().Be(new TimeOnly(16, 0));

        collisions.Should().HaveCount(2);
    }

    [Test]
    public void E2E_ReplacementForSameClientAsOriginal_AppearsInTimeline()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var work = CreateWork(clientId: clientId, start: new TimeOnly(8, 0), end: new TimeOnly(16, 0));
        var replStart = CreateWorkChange(work.Id, WorkChangeType.ReplacementStart,
            new TimeOnly(8, 0), new TimeOnly(10, 0), clientId);

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work], [replStart], []);
        var clientRects = rects.Where(r => r.ClientId == clientId).ToList();

        // Assert
        clientRects.Should().HaveCount(2);
        clientRects.Should().Contain(r => r.SourceType == TimeRectSourceType.Work);
        clientRects.Should().Contain(r => r.SourceType == TimeRectSourceType.Replacement);
    }

    [Test]
    public void E2E_MultipleWorksWithChanges_ComplexCollisionPattern()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var replaceClient = Guid.NewGuid();

        var work1 = CreateWork(clientId: clientId, start: new TimeOnly(6, 0), end: new TimeOnly(14, 0));
        var work2 = CreateWork(clientId: clientId, start: new TimeOnly(15, 0), end: new TimeOnly(20, 0));
        var work3 = CreateWork(clientId: replaceClient, start: new TimeOnly(10, 0), end: new TimeOnly(18, 0));

        var corrStart1 = CreateWorkChange(work1.Id, WorkChangeType.CorrectionStart,
            new TimeOnly(7, 0), new TimeOnly(7, 0));
        var replEnd3 = CreateWorkChange(work3.Id, WorkChangeType.ReplacementEnd,
            new TimeOnly(16, 0), new TimeOnly(18, 0), clientId);

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects(
            [work1, work2, work3], [corrStart1, replEnd3], []);

        var timeline = new ClientDayTimeline(clientId, TestDate);
        timeline.Rects.AddRange(rects.Where(r => r.ClientId == clientId && r.Date == TestDate));
        var collisions = timeline.GetCollisions();

        // Assert
        var clientRects = rects.Where(r => r.ClientId == clientId && r.Date == TestDate).ToList();
        clientRects.Should().Contain(r => r.SourceType == TimeRectSourceType.Work && r.SourceId == work1.Id);
        clientRects.Should().Contain(r => r.SourceType == TimeRectSourceType.Work && r.SourceId == work2.Id);
        clientRects.Should().Contain(r => r.SourceType == TimeRectSourceType.Replacement && r.SourceId == work3.Id);
        clientRects.Should().Contain(r => r.SourceType == TimeRectSourceType.Correction && r.SourceId == work1.Id);

        collisions.Should().HaveCount(1);
        var collision = collisions[0];
        var collisionIds = new[] { collision.A.SourceId, collision.B.SourceId };
        collisionIds.Should().Contain(work2.Id);
        collisionIds.Should().Contain(work3.Id);
    }

    [Test]
    public void E2E_TwoWorksWithReplacements_ReplacementsCollide()
    {
        // Arrange
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        var replaceClient = Guid.NewGuid();

        var work1 = CreateWork(clientId: clientA, start: new TimeOnly(8, 0), end: new TimeOnly(14, 0));
        var work2 = CreateWork(clientId: clientB, start: new TimeOnly(10, 0), end: new TimeOnly(16, 0));

        var repl1 = CreateWorkChange(work1.Id, WorkChangeType.ReplacementEnd,
            new TimeOnly(12, 0), new TimeOnly(14, 0), replaceClient);
        var repl2 = CreateWorkChange(work2.Id, WorkChangeType.ReplacementStart,
            new TimeOnly(10, 0), new TimeOnly(13, 0), replaceClient);

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects(
            [work1, work2], [repl1, repl2], []);

        var timeline = new ClientDayTimeline(replaceClient, TestDate);
        timeline.Rects.AddRange(rects.Where(r => r.ClientId == replaceClient && r.Date == TestDate));
        var collisions = timeline.GetCollisions();

        // Assert
        var replRects = rects.Where(r => r.SourceType == TimeRectSourceType.Replacement).ToList();
        replRects.Should().HaveCount(2);
        collisions.Should().HaveCount(1);
    }

    #endregion

    #region Edge Cases

    [Test]
    public void ZeroDurationWork_SingleRect()
    {
        // Arrange
        var work = CreateWork(start: new TimeOnly(12, 0), end: new TimeOnly(12, 0));

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work], [], []);

        // Assert
        rects.Should().HaveCount(1);
        rects[0].Start.Should().Be(new TimeOnly(12, 0));
        rects[0].End.Should().Be(new TimeOnly(12, 0));
    }

    [Test]
    public void ZeroDurationCorrection_SingleRect_NoMidnightSplit()
    {
        // Arrange
        var work = CreateWork(start: new TimeOnly(8, 0), end: new TimeOnly(16, 0));
        var corrStart = CreateWorkChange(work.Id, WorkChangeType.CorrectionStart,
            new TimeOnly(10, 0), new TimeOnly(10, 0));

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work], [corrStart], []);

        // Assert
        var corrRects = rects.Where(r => r.SourceType == TimeRectSourceType.Correction).ToList();
        corrRects.Should().HaveCount(1);
        corrRects[0].Date.Should().Be(TestDate);
    }

    [Test]
    public void EmptyInputs_NoRects()
    {
        // Arrange & Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([], [], []);

        // Assert
        rects.Should().BeEmpty();
    }

    [Test]
    public void WorkWithNoChanges_MultipleWorks_RectCountMatchesWorkCount()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var works = Enumerable.Range(0, 5).Select(i =>
            CreateWork(clientId: clientId,
                start: new TimeOnly(i * 3, 0),
                end: new TimeOnly(i * 3 + 2, 0))).ToList();

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects(works, [], []);

        // Assert
        rects.Should().HaveCount(5);
        rects.Should().OnlyContain(r => r.SourceType == TimeRectSourceType.Work);
    }

    [Test]
    public void Work_ChangeForDifferentWorkId_Ignored()
    {
        // Arrange
        var work = CreateWork(start: new TimeOnly(8, 0), end: new TimeOnly(16, 0));
        var unrelatedChange = CreateWorkChange(Guid.NewGuid(), WorkChangeType.CorrectionStart,
            new TimeOnly(10, 0), new TimeOnly(10, 0));

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work], [unrelatedChange], []);

        // Assert
        rects.Should().HaveCount(1);
        var workRect = rects.Single();
        workRect.SourceType.Should().Be(TimeRectSourceType.Work);
        workRect.Start.Should().Be(new TimeOnly(8, 0));
        workRect.End.Should().Be(new TimeOnly(16, 0));
    }

    #endregion

    #region Multi-Day Scenarios

    [Test]
    public void WorksOnDifferentDays_SameClient_NoCollision()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var day1 = new DateOnly(2025, 1, 15);
        var day2 = new DateOnly(2025, 1, 16);
        var work1 = CreateWork(clientId: clientId, date: day1, start: new TimeOnly(8, 0), end: new TimeOnly(16, 0));
        var work2 = CreateWork(clientId: clientId, date: day2, start: new TimeOnly(8, 0), end: new TimeOnly(16, 0));

        // Act
        var collisionsDay1 = RunFullPipeline([work1, work2], [], [], clientId, day1);
        var collisionsDay2 = RunFullPipeline([work1, work2], [], [], clientId, day2);

        // Assert
        collisionsDay1.Should().BeEmpty();
        collisionsDay2.Should().BeEmpty();
    }

    [Test]
    public void OvernightWork_CollidesWithNextDayMorningWork()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var day1 = new DateOnly(2025, 1, 15);
        var day2 = new DateOnly(2025, 1, 16);
        var nightWork = CreateWork(clientId: clientId, date: day1, start: new TimeOnly(22, 0), end: new TimeOnly(6, 0));
        var morningWork = CreateWork(clientId: clientId, date: day2, start: new TimeOnly(4, 0), end: new TimeOnly(12, 0));

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([nightWork, morningWork], [], []);
        var timelineDay2 = new ClientDayTimeline(clientId, day2);
        timelineDay2.Rects.AddRange(rects.Where(r => r.ClientId == clientId && r.Date == day2));
        var collisions = timelineDay2.GetCollisions();

        // Assert
        timelineDay2.Rects.Should().HaveCount(2);
        collisions.Should().HaveCount(1);
    }

    #endregion

    #region Rect Counts Verification

    [Test]
    public void Work_WithAllChangeTypes_CorrectTotalRectCount()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var replClientA = Guid.NewGuid();
        var replClientB = Guid.NewGuid();
        var work = CreateWork(clientId: clientId, start: new TimeOnly(6, 0), end: new TimeOnly(20, 0));
        var changes = new List<WorkChange>
        {
            CreateWorkChange(work.Id, WorkChangeType.CorrectionStart, new TimeOnly(7, 0), new TimeOnly(7, 0)),
            CreateWorkChange(work.Id, WorkChangeType.CorrectionEnd, new TimeOnly(19, 0), new TimeOnly(19, 0)),
            CreateWorkChange(work.Id, WorkChangeType.ReplacementStart, new TimeOnly(6, 0), new TimeOnly(8, 0), replClientA),
            CreateWorkChange(work.Id, WorkChangeType.ReplacementEnd, new TimeOnly(18, 0), new TimeOnly(20, 0), replClientB)
        };

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work], changes, []);

        // Assert
        rects.Where(r => r.SourceType == TimeRectSourceType.Work).Should().HaveCount(1);
        rects.Where(r => r.SourceType == TimeRectSourceType.Correction).Should().HaveCount(2);
        rects.Where(r => r.SourceType == TimeRectSourceType.Replacement).Should().HaveCount(2);
        rects.Should().HaveCount(5);
    }

    [Test]
    public void MultipleWorks_EachWithChanges_CorrectRectCounts()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var replClient = Guid.NewGuid();

        var work1 = CreateWork(clientId: clientId, start: new TimeOnly(6, 0), end: new TimeOnly(12, 0));
        var work2 = CreateWork(clientId: clientId, start: new TimeOnly(14, 0), end: new TimeOnly(20, 0));

        var corr1 = CreateWorkChange(work1.Id, WorkChangeType.CorrectionStart,
            new TimeOnly(7, 0), new TimeOnly(7, 0));
        var repl2 = CreateWorkChange(work2.Id, WorkChangeType.ReplacementEnd,
            new TimeOnly(18, 0), new TimeOnly(20, 0), replClient);

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects(
            [work1, work2], [corr1, repl2], []);

        // Assert
        rects.Where(r => r.SourceType == TimeRectSourceType.Work).Should().HaveCount(2);
        rects.Where(r => r.SourceType == TimeRectSourceType.Correction).Should().HaveCount(1);
        rects.Where(r => r.SourceType == TimeRectSourceType.Replacement).Should().HaveCount(1);
        rects.Should().HaveCount(4);
    }

    [Test]
    public void WorksAndBreaks_Mixed_CorrectRectCounts()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var work1 = CreateWork(clientId: clientId, start: new TimeOnly(8, 0), end: new TimeOnly(12, 0));
        var work2 = CreateWork(clientId: clientId, start: new TimeOnly(14, 0), end: new TimeOnly(18, 0));
        var break1 = CreateBreak(clientId: clientId, start: new TimeOnly(12, 0), end: new TimeOnly(12, 30));
        var break2 = CreateBreak(clientId: clientId, start: new TimeOnly(13, 0), end: new TimeOnly(13, 30));

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work1, work2], [], [break1, break2]);

        // Assert
        rects.Where(r => r.SourceType == TimeRectSourceType.Work).Should().HaveCount(2);
        rects.Where(r => r.SourceType == TimeRectSourceType.Break).Should().HaveCount(2);
        rects.Should().HaveCount(4);
    }

    #endregion

    #region Realistic Scenarios

    [Test]
    public void RealisticDay_MorningShiftWithBreakAndCorrection()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var work = CreateWork(clientId: clientId, start: new TimeOnly(7, 0), end: new TimeOnly(15, 30));
        var corrStart = CreateWorkChange(work.Id, WorkChangeType.CorrectionStart,
            new TimeOnly(7, 15), new TimeOnly(7, 15));
        var breakEntry = CreateBreak(clientId: clientId, start: new TimeOnly(12, 0), end: new TimeOnly(12, 30));

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work], [corrStart], [breakEntry]);

        // Assert
        var workRect = rects.Single(r => r.SourceType == TimeRectSourceType.Work);
        workRect.Start.Should().Be(new TimeOnly(7, 15));
        workRect.End.Should().Be(new TimeOnly(15, 30));

        rects.Should().Contain(r => r.SourceType == TimeRectSourceType.Correction);
        rects.Should().Contain(r => r.SourceType == TimeRectSourceType.Break);
        rects.Should().HaveCount(3);
    }

    [Test]
    public void RealisticDay_NightShiftReplacedAtStart()
    {
        // Arrange
        var originalClient = Guid.NewGuid();
        var replaceClient = Guid.NewGuid();
        var work = CreateWork(clientId: originalClient, start: new TimeOnly(22, 0), end: new TimeOnly(6, 0));
        var replStart = CreateWorkChange(work.Id, WorkChangeType.ReplacementStart,
            new TimeOnly(22, 0), new TimeOnly(0, 0), replaceClient);

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work], [replStart], []);

        // Assert
        var workRects = rects.Where(r => r.SourceType == TimeRectSourceType.Work).ToList();
        var replRects = rects.Where(r => r.SourceType == TimeRectSourceType.Replacement).ToList();

        workRects.Should().HaveCount(1);
        workRects[0].Start.Should().Be(new TimeOnly(0, 0));
        workRects[0].End.Should().Be(new TimeOnly(6, 0));

        replRects.Should().HaveCount(2);
    }

    [Test]
    public void RealisticDay_TwoClientsSwapSecondHalf_NoCollision()
    {
        // Arrange
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();

        var workA = CreateWork(clientId: clientA, start: new TimeOnly(8, 0), end: new TimeOnly(16, 0));
        var workB = CreateWork(clientId: clientB, start: new TimeOnly(8, 0), end: new TimeOnly(16, 0));

        var replEndA = CreateWorkChange(workA.Id, WorkChangeType.ReplacementEnd,
            new TimeOnly(12, 0), new TimeOnly(16, 0), clientB);
        var replEndB = CreateWorkChange(workB.Id, WorkChangeType.ReplacementEnd,
            new TimeOnly(12, 0), new TimeOnly(16, 0), clientA);

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects(
            [workA, workB], [replEndA, replEndB], []);

        var timelineA = new ClientDayTimeline(clientA, TestDate);
        timelineA.Rects.AddRange(rects.Where(r => r.ClientId == clientA && r.Date == TestDate));

        var timelineB = new ClientDayTimeline(clientB, TestDate);
        timelineB.Rects.AddRange(rects.Where(r => r.ClientId == clientB && r.Date == TestDate));

        // Assert
        timelineA.Rects.Where(r => r.SourceType == TimeRectSourceType.Work).Should().HaveCount(1);
        timelineA.Rects.Where(r => r.SourceType == TimeRectSourceType.Replacement).Should().HaveCount(1);
        timelineA.GetCollisions().Should().BeEmpty();

        timelineB.Rects.Where(r => r.SourceType == TimeRectSourceType.Work).Should().HaveCount(1);
        timelineB.Rects.Where(r => r.SourceType == TimeRectSourceType.Replacement).Should().HaveCount(1);
        timelineB.GetCollisions().Should().BeEmpty();
    }

    [Test]
    public void RealisticDay_DoubleBooking_DetectedViaCollision()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var morningShift = CreateWork(clientId: clientId, start: new TimeOnly(6, 0), end: new TimeOnly(14, 0));
        var afternoonShift = CreateWork(clientId: clientId, start: new TimeOnly(13, 0), end: new TimeOnly(21, 0));
        var breakEntry = CreateBreak(clientId: clientId, start: new TimeOnly(12, 0), end: new TimeOnly(12, 30));

        // Act
        var collisions = RunFullPipeline([morningShift, afternoonShift], [], [breakEntry], clientId);

        // Assert
        collisions.Should().HaveCount(1);
        var collision = collisions[0];
        var sourceIds = new[] { collision.A.SourceId, collision.B.SourceId };
        sourceIds.Should().Contain(morningShift.Id);
        sourceIds.Should().Contain(afternoonShift.Id);
    }

    [Test]
    public void RealisticDay_CorrectionFixesCollision()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var work1 = CreateWork(clientId: clientId, start: new TimeOnly(8, 0), end: new TimeOnly(14, 0));
        var work2 = CreateWork(clientId: clientId, start: new TimeOnly(12, 0), end: new TimeOnly(18, 0));
        var corrEnd = CreateWorkChange(work1.Id, WorkChangeType.CorrectionEnd,
            new TimeOnly(12, 0), new TimeOnly(12, 0));

        // Act
        var collisions = RunFullPipeline([work1, work2], [corrEnd], [], clientId);

        // Assert
        collisions.Should().BeEmpty();
    }

    [Test]
    public void RealisticDay_ReplacementCreatesNewCollision()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var otherClient = Guid.NewGuid();

        var work1 = CreateWork(clientId: clientId, start: new TimeOnly(8, 0), end: new TimeOnly(16, 0));
        var work2 = CreateWork(clientId: otherClient, start: new TimeOnly(6, 0), end: new TimeOnly(14, 0));
        var replEnd = CreateWorkChange(work2.Id, WorkChangeType.ReplacementEnd,
            new TimeOnly(10, 0), new TimeOnly(14, 0), clientId);

        // Act
        var collisions = RunFullPipeline([work1, work2], [replEnd], [], clientId);

        // Assert
        collisions.Should().HaveCount(1);
    }

    #endregion
}
