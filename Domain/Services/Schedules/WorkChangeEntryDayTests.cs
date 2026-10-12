// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Services.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Schedules;

/// <summary>
/// WorkChangeEntryDay must place a work change on the same day get_schedule_entries shows it on: on a night shift
/// (22:00-06:00) the post-midnight parts move to the following day, everything else and every change of a day shift
/// stays on the work's day.
/// </summary>
[TestFixture]
public sealed class WorkChangeEntryDayTests
{
    private static readonly DateOnly Day = new(2026, 5, 11);
    private static readonly TimeOnly NightStart = new(22, 0);
    private static readonly TimeOnly NightEnd = new(6, 0);
    private static readonly TimeOnly DayStart = new(8, 0);
    private static readonly TimeOnly DayEnd = new(16, 0);

    [TestCase(WorkChangeType.ReplacementEnd, 0, 2, 1)]
    [TestCase(WorkChangeType.ReplacementEnd, 0, 8, 0)]
    [TestCase(WorkChangeType.ReplacementWithin, 1, 2, 1)]
    [TestCase(WorkChangeType.ReplacementWithin, 23, 1, 0)]
    [TestCase(WorkChangeType.ReplacementStart, 0, 8, 0)]
    [TestCase(WorkChangeType.TravelWithin, 3, 1, 1)]
    [TestCase(WorkChangeType.TravelWithin, 23, 1, 0)]
    [TestCase(WorkChangeType.CorrectionEnd, 0, 1, 1)]
    [TestCase(WorkChangeType.TravelEnd, 0, 1, 1)]
    [TestCase(WorkChangeType.Debriefing, 0, 1, 1)]
    [TestCase(WorkChangeType.CorrectionStart, 0, 1, 0)]
    [TestCase(WorkChangeType.Briefing, 0, 1, 0)]
    [TestCase(WorkChangeType.TravelStart, 0, 1, 0)]
    public void NightShift_PlacesTheChangeLikeTheSchedule(WorkChangeType type, int changeStartHour, decimal hours, int expectedOffset)
    {
        var day = WorkChangeEntryDay.Of(type, Day, NightEnd, new TimeOnly(changeStartHour, 0), hours, NightStart, NightEnd);

        day.ShouldBe(Day.AddDays(expectedOffset));
    }

    [TestCase(WorkChangeType.ReplacementEnd)]
    [TestCase(WorkChangeType.ReplacementWithin)]
    [TestCase(WorkChangeType.CorrectionEnd)]
    [TestCase(WorkChangeType.TravelWithin)]
    public void DayShift_AlwaysStaysOnTheWorksDay(WorkChangeType type)
    {
        WorkChangeEntryDay.Of(type, Day, DayEnd, new TimeOnly(1, 0), 2m, DayStart, DayEnd).ShouldBe(Day);
    }
}
