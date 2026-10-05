// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the absolute placement of correction and replacement blocks around midnight: a briefing before a work that
/// starts just after midnight belongs to the previous evening, a debriefing after a night shift to the next morning,
/// replacements of a night shift land on the right calendar day, and a work fully covered by replacements leaves no
/// 24-hour work block behind. A 24-hour work (start == end) without changes stays 24 hours.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Services.Schedules;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class TimelineCalculationServiceMidnightTests
{
    private static readonly DateOnly July31 = new(2026, 7, 31);
    private static readonly DateOnly August1 = new(2026, 8, 1);
    private static readonly DateOnly August2 = new(2026, 8, 2);

    private TimelineCalculationService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _service = new TimelineCalculationService(
            Options.Create(new ScheduleTimeOptions()),
            NullLogger<TimelineCalculationService>.Instance);
    }

    [Test]
    public void BriefingBeforeWorkStartingAfterMidnight_IsPlacedOnThePreviousEvening()
    {
        var work = CreateWork(August1, new TimeOnly(0, 30), new TimeOnly(8, 0));
        var briefing = CreateChange(work, WorkChangeType.Briefing, 1m);

        var blocks = _service.CalculateScheduleBlocks([work], [briefing], []);

        var correction = blocks.Single(b => b.BlockType == ScheduleBlockType.Correction);
        correction.Start.ShouldBe(July31.ToDateTime(new TimeOnly(23, 30)));
        correction.End.ShouldBe(August1.ToDateTime(new TimeOnly(0, 30)));
    }

    [Test]
    public void DebriefingAfterNightShift_IsPlacedOnTheNextMorning()
    {
        var work = CreateWork(July31, new TimeOnly(22, 0), new TimeOnly(6, 0));
        var debriefing = CreateChange(work, WorkChangeType.Debriefing, 1m);

        var blocks = _service.CalculateScheduleBlocks([work], [debriefing], []);

        var correction = blocks.Single(b => b.BlockType == ScheduleBlockType.Correction);
        correction.Start.ShouldBe(August1.ToDateTime(new TimeOnly(6, 0)));
        correction.End.ShouldBe(August1.ToDateTime(new TimeOnly(7, 0)));
    }

    [Test]
    public void FullReplacementOfDayShift_LeavesNoWorkBlock()
    {
        var work = CreateWork(August1, new TimeOnly(8, 0), new TimeOnly(16, 0));
        var replacement = CreateChange(work, WorkChangeType.ReplacementStart, 8m, Guid.NewGuid());

        var blocks = _service.CalculateScheduleBlocks([work], [replacement], []);

        blocks.ShouldNotContain(b => b.BlockType == ScheduleBlockType.Work);
        var replacementBlock = blocks.Single(b => b.BlockType == ScheduleBlockType.Replacement);
        replacementBlock.Start.ShouldBe(August1.ToDateTime(new TimeOnly(8, 0)));
        replacementBlock.End.ShouldBe(August1.ToDateTime(new TimeOnly(16, 0)));
    }

    [Test]
    public void FullReplacementOfNightShiftFromTheEnd_CoversTheNightAndLeavesNoWorkBlock()
    {
        var work = CreateWork(July31, new TimeOnly(22, 0), new TimeOnly(6, 0));
        var replacement = CreateChange(work, WorkChangeType.ReplacementEnd, 8m, Guid.NewGuid());

        var blocks = _service.CalculateScheduleBlocks([work], [replacement], []);

        blocks.ShouldNotContain(b => b.BlockType == ScheduleBlockType.Work);
        var replacementBlock = blocks.Single(b => b.BlockType == ScheduleBlockType.Replacement);
        replacementBlock.Start.ShouldBe(July31.ToDateTime(new TimeOnly(22, 0)));
        replacementBlock.End.ShouldBe(August1.ToDateTime(new TimeOnly(6, 0)));
    }

    [Test]
    public void PartialReplacementAtTheEndOfNightShift_IsPlacedAfterMidnight()
    {
        var work = CreateWork(July31, new TimeOnly(22, 0), new TimeOnly(6, 0));
        var replacement = CreateChange(work, WorkChangeType.ReplacementEnd, 2m, Guid.NewGuid());

        var blocks = _service.CalculateScheduleBlocks([work], [replacement], []);

        var replacementBlock = blocks.Single(b => b.BlockType == ScheduleBlockType.Replacement);
        replacementBlock.Start.ShouldBe(August1.ToDateTime(new TimeOnly(4, 0)));
        replacementBlock.End.ShouldBe(August1.ToDateTime(new TimeOnly(6, 0)));
        var workBlock = blocks.Single(b => b.BlockType == ScheduleBlockType.Work);
        workBlock.Start.ShouldBe(July31.ToDateTime(new TimeOnly(22, 0)));
        workBlock.End.ShouldBe(August1.ToDateTime(new TimeOnly(4, 0)));
    }

    [Test]
    public void TwentyFourHourWorkWithoutChanges_StaysTwentyFourHours()
    {
        var work = CreateWork(August1, new TimeOnly(8, 0), new TimeOnly(8, 0));

        var blocks = _service.CalculateScheduleBlocks([work], [], []);

        var workBlock = blocks.Single();
        workBlock.Start.ShouldBe(August1.ToDateTime(new TimeOnly(8, 0)));
        workBlock.End.ShouldBe(August2.ToDateTime(new TimeOnly(8, 0)));
    }

    private static Work CreateWork(DateOnly date, TimeOnly start, TimeOnly end) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = Guid.NewGuid(),
        CurrentDate = date,
        StartTime = start,
        EndTime = end,
        ShiftId = Guid.NewGuid()
    };

    private static WorkChange CreateChange(Work work, WorkChangeType type, decimal hours, Guid? replaceClientId = null) => new()
    {
        Id = Guid.NewGuid(),
        WorkId = work.Id,
        Type = type,
        ChangeTime = hours,
        StartTime = TimeOnly.MinValue,
        EndTime = TimeOnly.MinValue,
        ReplaceClientId = replaceClientId
    };
}
