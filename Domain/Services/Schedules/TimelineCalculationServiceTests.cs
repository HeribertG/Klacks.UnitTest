using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class TimelineCalculationServiceTests
{
    private static readonly DateOnly BaseDate = new(2026, 3, 15);
    private TimelineCalculationService _service = null!;

    [SetUp]
    public void Setup()
    {
        _service = new TimelineCalculationService();
    }

    private Work CreateWork(TimeOnly start, TimeOnly end, Guid? clientId = null, Guid? id = null, DateOnly? date = null)
    {
        return new Work
        {
            Id = id ?? Guid.NewGuid(),
            ClientId = clientId ?? Guid.NewGuid(),
            CurrentDate = date ?? BaseDate,
            StartTime = start,
            EndTime = end,
            ShiftId = Guid.NewGuid()
        };
    }

    [Test]
    public void CalculateScheduleBlocks_NormalDayShift_ReturnsOneBlock()
    {
        // Arrange
        var work = CreateWork(new TimeOnly(8, 0), new TimeOnly(16, 0));

        // Act
        var blocks = _service.CalculateScheduleBlocks([work], [], []);

        // Assert
        blocks.Should().HaveCount(1);
        blocks[0].BlockType.Should().Be(ScheduleBlockType.Work);
        blocks[0].Start.Should().Be(BaseDate.ToDateTime(new TimeOnly(8, 0)));
        blocks[0].End.Should().Be(BaseDate.ToDateTime(new TimeOnly(16, 0)));
        blocks[0].Duration.Should().Be(TimeSpan.FromHours(8));
    }

    [Test]
    public void CalculateScheduleBlocks_NightShift_ReturnsOneUnsplitBlock()
    {
        // Arrange
        var work = CreateWork(new TimeOnly(22, 0), new TimeOnly(6, 0));

        // Act
        var blocks = _service.CalculateScheduleBlocks([work], [], []);

        // Assert
        blocks.Should().HaveCount(1);
        blocks[0].Start.Should().Be(BaseDate.ToDateTime(new TimeOnly(22, 0)));
        blocks[0].End.Should().Be(BaseDate.AddDays(1).ToDateTime(new TimeOnly(6, 0)));
        blocks[0].Duration.Should().Be(TimeSpan.FromHours(8));
    }

    [Test]
    public void CalculateScheduleBlocks_WithCorrectionStart_ChangesEffectiveStart()
    {
        // Arrange
        var work = CreateWork(new TimeOnly(8, 0), new TimeOnly(16, 0));
        var correction = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = WorkChangeType.CorrectionStart,
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(9, 0)
        };

        // Act
        var blocks = _service.CalculateScheduleBlocks([work], [correction], []);

        // Assert
        var workBlock = blocks.First(b => b.BlockType == ScheduleBlockType.Work);
        workBlock.Start.Should().Be(BaseDate.ToDateTime(new TimeOnly(9, 0)));
        workBlock.End.Should().Be(BaseDate.ToDateTime(new TimeOnly(16, 0)));

        var correctionBlock = blocks.First(b => b.BlockType == ScheduleBlockType.Correction);
        correctionBlock.Should().NotBeNull();
    }

    [Test]
    public void CalculateScheduleBlocks_WithCorrectionEnd_ChangesEffectiveEnd()
    {
        // Arrange
        var work = CreateWork(new TimeOnly(8, 0), new TimeOnly(16, 0));
        var correction = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = WorkChangeType.CorrectionEnd,
            StartTime = new TimeOnly(15, 0),
            EndTime = new TimeOnly(15, 0)
        };

        // Act
        var blocks = _service.CalculateScheduleBlocks([work], [correction], []);

        // Assert
        var workBlock = blocks.First(b => b.BlockType == ScheduleBlockType.Work);
        workBlock.Start.Should().Be(BaseDate.ToDateTime(new TimeOnly(8, 0)));
        workBlock.End.Should().Be(BaseDate.ToDateTime(new TimeOnly(15, 0)));
    }

    [Test]
    public void CalculateScheduleBlocks_WithReplacement_CreatesReplacementBlock()
    {
        // Arrange
        var work = CreateWork(new TimeOnly(8, 0), new TimeOnly(16, 0));
        var replaceClientId = Guid.NewGuid();
        var replacement = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = WorkChangeType.ReplacementStart,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(12, 0),
            ReplaceClientId = replaceClientId
        };

        // Act
        var blocks = _service.CalculateScheduleBlocks([work], [replacement], []);

        // Assert
        var replacementBlock = blocks.First(b => b.BlockType == ScheduleBlockType.Replacement);
        replacementBlock.ClientId.Should().Be(replaceClientId);
        replacementBlock.Start.Should().Be(BaseDate.ToDateTime(new TimeOnly(8, 0)));
        replacementBlock.End.Should().Be(BaseDate.ToDateTime(new TimeOnly(12, 0)));

        var workBlock = blocks.First(b => b.BlockType == ScheduleBlockType.Work);
        workBlock.Start.Should().Be(BaseDate.ToDateTime(new TimeOnly(12, 0)));
    }

    [Test]
    public void CalculateScheduleBlocks_BreakEntry_CreatesBreakBlock()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var breakEntry = new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            CurrentDate = BaseDate,
            StartTime = new TimeOnly(12, 0),
            EndTime = new TimeOnly(13, 0),
            AbsenceId = Guid.NewGuid()
        };

        // Act
        var blocks = _service.CalculateScheduleBlocks([], [], [breakEntry]);

        // Assert
        blocks.Should().HaveCount(1);
        blocks[0].BlockType.Should().Be(ScheduleBlockType.Break);
        blocks[0].ClientId.Should().Be(clientId);
        blocks[0].Start.Should().Be(BaseDate.ToDateTime(new TimeOnly(12, 0)));
        blocks[0].End.Should().Be(BaseDate.ToDateTime(new TimeOnly(13, 0)));
    }

    [Test]
    public void CalculateScheduleBlocks_ShiftEndingAtMidnight_EndsAtNextDayMidnight()
    {
        // Arrange
        var work = CreateWork(new TimeOnly(14, 0), new TimeOnly(0, 0));

        // Act
        var blocks = _service.CalculateScheduleBlocks([work], [], []);

        // Assert
        blocks.Should().HaveCount(1);
        blocks[0].Start.Should().Be(BaseDate.ToDateTime(new TimeOnly(14, 0)));
        blocks[0].End.Should().Be(BaseDate.AddDays(1).ToDateTime(TimeOnly.MinValue));
        blocks[0].Duration.Should().Be(TimeSpan.FromHours(10));
    }

    [Test]
    public void CalculateScheduleBlocks_ShiftStartingAtMidnight_StartsAtMidnight()
    {
        // Arrange
        var work = CreateWork(new TimeOnly(0, 0), new TimeOnly(8, 0));

        // Act
        var blocks = _service.CalculateScheduleBlocks([work], [], []);

        // Assert
        blocks.Should().HaveCount(1);
        blocks[0].Start.Should().Be(BaseDate.ToDateTime(TimeOnly.MinValue));
        blocks[0].End.Should().Be(BaseDate.ToDateTime(new TimeOnly(8, 0)));
        blocks[0].Duration.Should().Be(TimeSpan.FromHours(8));
    }

    [Test]
    public void CalculateScheduleBlocks_MultipleWorks_ReturnsAllBlocks()
    {
        // Arrange
        var work1 = CreateWork(new TimeOnly(6, 0), new TimeOnly(14, 0));
        var work2 = CreateWork(new TimeOnly(14, 0), new TimeOnly(22, 0));

        // Act
        var blocks = _service.CalculateScheduleBlocks([work1, work2], [], []);

        // Assert
        blocks.Should().HaveCount(2);
        blocks.Should().AllSatisfy(b => b.BlockType.Should().Be(ScheduleBlockType.Work));
    }

    [Test]
    public void CalculateScheduleBlocks_WorkWithNoChanges_UsesOriginalTimes()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var work = CreateWork(new TimeOnly(7, 30), new TimeOnly(15, 45), clientId);

        // Act
        var blocks = _service.CalculateScheduleBlocks([work], [], []);

        // Assert
        blocks.Should().HaveCount(1);
        blocks[0].SourceId.Should().Be(work.Id);
        blocks[0].ClientId.Should().Be(clientId);
        blocks[0].Start.Should().Be(BaseDate.ToDateTime(new TimeOnly(7, 30)));
        blocks[0].End.Should().Be(BaseDate.ToDateTime(new TimeOnly(15, 45)));
    }

    [Test]
    public void CalculateScheduleBlocks_ReplacementWithoutClientId_NoReplacementBlock()
    {
        // Arrange
        var work = CreateWork(new TimeOnly(8, 0), new TimeOnly(16, 0));
        var replacement = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = WorkChangeType.ReplacementEnd,
            StartTime = new TimeOnly(14, 0),
            EndTime = new TimeOnly(16, 0),
            ReplaceClientId = null
        };

        // Act
        var blocks = _service.CalculateScheduleBlocks([work], [replacement], []);

        // Assert
        blocks.Should().NotContain(b => b.BlockType == ScheduleBlockType.Replacement);
    }

    [Test]
    public void CalculateScheduleBlocks_EmptyInputs_ReturnsEmpty()
    {
        // Arrange & Act
        var blocks = _service.CalculateScheduleBlocks([], [], []);

        // Assert
        blocks.Should().BeEmpty();
    }

    [Test]
    public void CalculateScheduleBlocks_NightBreak_CrossesMidnight()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var breakEntry = new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            CurrentDate = BaseDate,
            StartTime = new TimeOnly(23, 0),
            EndTime = new TimeOnly(1, 0),
            AbsenceId = Guid.NewGuid()
        };

        // Act
        var blocks = _service.CalculateScheduleBlocks([], [], [breakEntry]);

        // Assert
        blocks.Should().HaveCount(1);
        blocks[0].Start.Should().Be(BaseDate.ToDateTime(new TimeOnly(23, 0)));
        blocks[0].End.Should().Be(BaseDate.AddDays(1).ToDateTime(new TimeOnly(1, 0)));
        blocks[0].Duration.Should().Be(TimeSpan.FromHours(2));
    }
}
