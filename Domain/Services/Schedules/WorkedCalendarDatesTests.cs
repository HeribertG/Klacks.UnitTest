// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for WorkedCalendarDates, the single rule for which calendar days a work actually covers: every day its
/// company-local interval touches (end exclusive), from schedule blocks as well as from planned rows, with the same
/// midnight wrap as TimelineCalculationService (end at or before start continues on the next day).
/// </summary>

using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class WorkedCalendarDatesTests
{
    private static readonly DateOnly July31 = new(2026, 7, 31);
    private static readonly DateOnly August1 = new(2026, 8, 1);

    [Test]
    public void FromTimeRange_DayShift_TouchesOnlyItsDate()
    {
        WorkedCalendarDates.FromTimeRange(July31, new TimeOnly(8, 0), new TimeOnly(16, 0))
            .ShouldBe([July31]);
    }

    [Test]
    public void FromTimeRange_NightShiftAcrossMidnight_TouchesBothDates()
    {
        WorkedCalendarDates.FromTimeRange(July31, new TimeOnly(22, 0), new TimeOnly(6, 0))
            .ShouldBe([July31, August1]);
    }

    [Test]
    public void FromTimeRange_EndingExactlyAtMidnight_DoesNotTouchNextDay()
    {
        WorkedCalendarDates.FromTimeRange(July31, new TimeOnly(16, 0), TimeOnly.MinValue)
            .ShouldBe([July31]);
    }

    [Test]
    public void FromTimeRange_StartEqualsEnd_IsTwentyFourHoursLikeTheTimeline()
    {
        WorkedCalendarDates.FromTimeRange(July31, new TimeOnly(8, 0), new TimeOnly(8, 0))
            .ShouldBe([July31, August1]);
    }

    [Test]
    public void FromBlocks_NightWorkBlock_TouchesBothDates()
    {
        var block = Block(ScheduleBlockType.Work, July31.ToDateTime(new TimeOnly(22, 0)), August1.ToDateTime(new TimeOnly(6, 0)));

        WorkedCalendarDates.FromBlocks([block]).ShouldBe([July31, August1]);
    }

    [Test]
    public void FromBlocks_BreakBlock_IsNotWork()
    {
        var block = Block(ScheduleBlockType.Break, August1.ToDateTime(new TimeOnly(8, 0)), August1.ToDateTime(new TimeOnly(16, 0)));

        WorkedCalendarDates.FromBlocks([block]).ShouldBeEmpty();
    }

    [TestCase(ScheduleBlockType.Correction)]
    [TestCase(ScheduleBlockType.Replacement)]
    public void FromBlocks_CorrectionAndReplacement_CountAsWork(ScheduleBlockType blockType)
    {
        var block = Block(blockType, July31.ToDateTime(new TimeOnly(23, 0)), August1.ToDateTime(new TimeOnly(1, 0)));

        WorkedCalendarDates.FromBlocks([block]).ShouldBe([July31, August1]);
    }

    [Test]
    public void FromBlocks_SeveralBlocksOnSameDates_AreDistinctAndOrdered()
    {
        var night = Block(ScheduleBlockType.Work, July31.ToDateTime(new TimeOnly(22, 0)), August1.ToDateTime(new TimeOnly(6, 0)));
        var day = Block(ScheduleBlockType.Work, August1.ToDateTime(new TimeOnly(14, 0)), August1.ToDateTime(new TimeOnly(20, 0)));

        WorkedCalendarDates.FromBlocks([day, night]).ShouldBe([July31, August1]);
    }

    [Test]
    public void FromBlocks_DstAwareBlock_UsesCompanyLocalCalendar()
    {
        var localStart = August1.ToDateTime(new TimeOnly(0, 30));
        var localEnd = August1.ToDateTime(new TimeOnly(8, 0));
        var block = new ScheduleBlock(
            Guid.NewGuid(), ScheduleBlockType.Work, Guid.NewGuid(),
            July31.ToDateTime(new TimeOnly(22, 30)), August1.ToDateTime(new TimeOnly(6, 0)),
            LocalStart: localStart, LocalEnd: localEnd);

        WorkedCalendarDates.FromBlocks([block]).ShouldBe([August1]);
    }

    [Test]
    public void FromBlocks_BlockEndingAtMidnight_DoesNotTouchNextDay()
    {
        var block = Block(ScheduleBlockType.Work, July31.ToDateTime(new TimeOnly(16, 0)), August1.ToDateTime(TimeOnly.MinValue));

        WorkedCalendarDates.FromBlocks([block]).ShouldBe([July31]);
    }

    private static ScheduleBlock Block(ScheduleBlockType blockType, DateTime start, DateTime end)
        => new(Guid.NewGuid(), blockType, Guid.NewGuid(), start, end);
}
