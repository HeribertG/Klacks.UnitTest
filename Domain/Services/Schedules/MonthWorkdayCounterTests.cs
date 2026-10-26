// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the workday count of a month: configured weekend days and the caller's holidays are excluded, a missing
/// holiday rule excludes weekends only.
/// </summary>

using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class MonthWorkdayCounterTests
{
    private static readonly IReadOnlySet<DayOfWeek> SaturdaySunday = new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday };

    [Test]
    public void Count_November2026_WithoutHolidays_Is21()
    {
        MonthWorkdayCounter.Count(2026, 11, SaturdaySunday, null).ShouldBe(21);
    }

    [Test]
    public void Count_HolidaysOnWorkdays_AreExcluded_HolidaysOnWeekends_ChangeNothing()
    {
        var holidays = new HashSet<DateOnly> { new(2026, 12, 25), new(2026, 12, 26) };

        MonthWorkdayCounter.Count(2026, 12, SaturdaySunday, holidays.Contains).ShouldBe(22);
    }

    [Test]
    public void Count_FridayWeekend_UsesTheConfiguredDays()
    {
        var fridaySaturday = new HashSet<DayOfWeek> { DayOfWeek.Friday, DayOfWeek.Saturday };

        MonthWorkdayCounter.Count(2026, 11, fridaySaturday, null).ShouldBe(22);
    }
}
