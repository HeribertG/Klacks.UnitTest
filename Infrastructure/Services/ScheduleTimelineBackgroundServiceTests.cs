using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Services;

namespace Klacks.UnitTest.Infrastructure.Services;

[TestFixture]
public class ScheduleTimelineBackgroundServiceTests
{
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
            CurrentDate = date ?? new DateOnly(2025, 1, 15),
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

    [Test]
    public void CalculateTimeRects_WorkWithoutChanges_ReturnsOneWorkRect()
    {
        // Arrange
        var work = CreateWork(start: new TimeOnly(8, 0), end: new TimeOnly(16, 0));

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work], [], []);

        // Assert
        rects.Should().HaveCount(1);
        rects[0].SourceId.Should().Be(work.Id);
        rects[0].SourceType.Should().Be(TimeRectSourceType.Work);
        rects[0].Start.Should().Be(new TimeOnly(8, 0));
        rects[0].End.Should().Be(new TimeOnly(16, 0));
    }

    [Test]
    public void CalculateTimeRects_WorkWithCorrectionStart_AdjustsStartAndCreatesCorrectionRect()
    {
        // Arrange
        var work = CreateWork(start: new TimeOnly(8, 0), end: new TimeOnly(16, 0));
        var change = CreateWorkChange(work.Id, WorkChangeType.CorrectionStart,
            new TimeOnly(9, 0), new TimeOnly(9, 0));

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work], [change], []);

        // Assert
        rects.Should().HaveCount(2);
        var workRect = rects.Single(r => r.SourceType == TimeRectSourceType.Work);
        workRect.Start.Should().Be(new TimeOnly(9, 0));
        workRect.End.Should().Be(new TimeOnly(16, 0));

        var correctionRect = rects.Single(r => r.SourceType == TimeRectSourceType.Correction);
        correctionRect.SourceId.Should().Be(work.Id);
        correctionRect.ClientId.Should().Be(work.ClientId);
        correctionRect.Start.Should().Be(new TimeOnly(9, 0));
        correctionRect.End.Should().Be(new TimeOnly(9, 0));
    }

    [Test]
    public void CalculateTimeRects_WorkWithCorrectionEnd_AdjustsEndAndCreatesCorrectionRect()
    {
        // Arrange
        var work = CreateWork(start: new TimeOnly(8, 0), end: new TimeOnly(16, 0));
        var change = CreateWorkChange(work.Id, WorkChangeType.CorrectionEnd,
            new TimeOnly(14, 0), new TimeOnly(14, 0));

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work], [change], []);

        // Assert
        rects.Should().HaveCount(2);
        var workRect = rects.Single(r => r.SourceType == TimeRectSourceType.Work);
        workRect.Start.Should().Be(new TimeOnly(8, 0));
        workRect.End.Should().Be(new TimeOnly(14, 0));

        var correctionRect = rects.Single(r => r.SourceType == TimeRectSourceType.Correction);
        correctionRect.SourceId.Should().Be(work.Id);
        correctionRect.ClientId.Should().Be(work.ClientId);
        correctionRect.Start.Should().Be(new TimeOnly(14, 0));
        correctionRect.End.Should().Be(new TimeOnly(14, 0));
    }

    [Test]
    public void CalculateTimeRects_WorkWithReplacementStart_ShortenedOriginalAndReplacementRect()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var replaceClientId = Guid.NewGuid();
        var work = CreateWork(clientId: clientId, start: new TimeOnly(8, 0), end: new TimeOnly(16, 0));
        var change = CreateWorkChange(work.Id, WorkChangeType.ReplacementStart,
            new TimeOnly(8, 0), new TimeOnly(10, 0), replaceClientId);

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work], [change], []);

        // Assert
        var workRect = rects.Single(r => r.SourceType == TimeRectSourceType.Work);
        workRect.Start.Should().Be(new TimeOnly(10, 0));
        workRect.End.Should().Be(new TimeOnly(16, 0));

        var replacementRect = rects.Single(r => r.SourceType == TimeRectSourceType.Replacement);
        replacementRect.ClientId.Should().Be(replaceClientId);
        replacementRect.Start.Should().Be(new TimeOnly(8, 0));
        replacementRect.End.Should().Be(new TimeOnly(10, 0));
    }

    [Test]
    public void CalculateTimeRects_WorkWithReplacementEnd_ShortenedOriginalAndReplacementRect()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var replaceClientId = Guid.NewGuid();
        var work = CreateWork(clientId: clientId, start: new TimeOnly(8, 0), end: new TimeOnly(16, 0));
        var change = CreateWorkChange(work.Id, WorkChangeType.ReplacementEnd,
            new TimeOnly(14, 0), new TimeOnly(16, 0), replaceClientId);

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work], [change], []);

        // Assert
        var workRect = rects.Single(r => r.SourceType == TimeRectSourceType.Work);
        workRect.Start.Should().Be(new TimeOnly(8, 0));
        workRect.End.Should().Be(new TimeOnly(14, 0));

        var replacementRect = rects.Single(r => r.SourceType == TimeRectSourceType.Replacement);
        replacementRect.ClientId.Should().Be(replaceClientId);
        replacementRect.Start.Should().Be(new TimeOnly(14, 0));
        replacementRect.End.Should().Be(new TimeOnly(16, 0));
    }

    [Test]
    public void CalculateTimeRects_OvernightWork_CreatesTwoRects()
    {
        // Arrange
        var date = new DateOnly(2025, 1, 15);
        var work = CreateWork(date: date, start: new TimeOnly(22, 0), end: new TimeOnly(6, 0));

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([work], [], []);

        // Assert
        rects.Should().HaveCount(2);
        var dayRect = rects.Single(r => r.Date == date);
        dayRect.Start.Should().Be(new TimeOnly(22, 0));
        dayRect.End.Should().Be(TimeOnly.MaxValue);

        var nextDayRect = rects.Single(r => r.Date == date.AddDays(1));
        nextDayRect.Start.Should().Be(TimeOnly.MinValue);
        nextDayRect.End.Should().Be(new TimeOnly(6, 0));
    }

    [Test]
    public void CalculateTimeRects_BreakEntry_ReturnsBreakRect()
    {
        // Arrange
        var breakEntry = new Break
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            CurrentDate = new DateOnly(2025, 1, 15),
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            AbsenceId = Guid.NewGuid()
        };

        // Act
        var rects = ScheduleTimelineBackgroundService.CalculateTimeRects([], [], [breakEntry]);

        // Assert
        rects.Should().HaveCount(1);
        rects[0].SourceId.Should().Be(breakEntry.Id);
        rects[0].SourceType.Should().Be(TimeRectSourceType.Break);
        rects[0].Start.Should().Be(new TimeOnly(8, 0));
        rects[0].End.Should().Be(new TimeOnly(16, 0));
    }

    [Test]
    public void ClientDayTimeline_GetCollisions_OverlappingWorkRects_DetectsCollision()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var date = new DateOnly(2025, 1, 15);
        var timeline = new ClientDayTimeline(clientId, date);
        timeline.Rects.Add(new TimeRect(Guid.NewGuid(), TimeRectSourceType.Work, clientId, date,
            new TimeOnly(8, 0), new TimeOnly(14, 0)));
        timeline.Rects.Add(new TimeRect(Guid.NewGuid(), TimeRectSourceType.Work, clientId, date,
            new TimeOnly(12, 0), new TimeOnly(18, 0)));

        // Act
        var collisions = timeline.GetCollisions();

        // Assert
        collisions.Should().HaveCount(1);
    }

    [Test]
    public void ClientDayTimeline_GetCollisions_NonOverlappingWorkRects_NoCollision()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var date = new DateOnly(2025, 1, 15);
        var timeline = new ClientDayTimeline(clientId, date);
        timeline.Rects.Add(new TimeRect(Guid.NewGuid(), TimeRectSourceType.Work, clientId, date,
            new TimeOnly(8, 0), new TimeOnly(12, 0)));
        timeline.Rects.Add(new TimeRect(Guid.NewGuid(), TimeRectSourceType.Work, clientId, date,
            new TimeOnly(12, 0), new TimeOnly(18, 0)));

        // Act
        var collisions = timeline.GetCollisions();

        // Assert
        collisions.Should().BeEmpty();
    }

    [Test]
    public void ClientDayTimeline_GetCollisions_BreakRectsIgnored_NoCollision()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var date = new DateOnly(2025, 1, 15);
        var timeline = new ClientDayTimeline(clientId, date);
        timeline.Rects.Add(new TimeRect(Guid.NewGuid(), TimeRectSourceType.Work, clientId, date,
            new TimeOnly(8, 0), new TimeOnly(16, 0)));
        timeline.Rects.Add(new TimeRect(Guid.NewGuid(), TimeRectSourceType.Break, clientId, date,
            new TimeOnly(10, 0), new TimeOnly(14, 0)));

        // Act
        var collisions = timeline.GetCollisions();

        // Assert
        collisions.Should().BeEmpty();
    }

    [Test]
    public void ClientDayTimeline_GetCollisions_SameSourceId_Ignored()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var date = new DateOnly(2025, 1, 15);
        var timeline = new ClientDayTimeline(clientId, date);
        timeline.Rects.Add(new TimeRect(sourceId, TimeRectSourceType.Work, clientId, date,
            new TimeOnly(8, 0), new TimeOnly(14, 0)));
        timeline.Rects.Add(new TimeRect(sourceId, TimeRectSourceType.Replacement, clientId, date,
            new TimeOnly(12, 0), new TimeOnly(18, 0)));

        // Act
        var collisions = timeline.GetCollisions();

        // Assert
        collisions.Should().BeEmpty();
    }
}
