// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the biweekly pay-period rule: a fixed 14-day cycle anchored on Monday 2025-12-29, aligned to the configured
/// week start, without ISO week numbers. With a Monday week start it matches the former ISO-parity rule on every day
/// from 2021-01-04 to 2027-01-03 and, unlike that rule, does not overlap at the end of the 53-week ISO year 2026.
/// The same fixed dates are pinned by the Ui parity spec (calendar-util.service.biweekly.spec.ts).
/// </summary>

using System.Globalization;
using Klacks.Api.Domain.Services.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class BiweeklyPayPeriodTests
{
    private static readonly DateOnly EquivalenceFirstDay = new(2021, 1, 4);
    private static readonly DateOnly EquivalenceLastDay = new(2027, 1, 3);

    private static DateOnly D(string isoDate) =>
        DateOnly.ParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static (DateOnly Start, DateOnly End) FormerIsoParityRule(DateOnly date)
    {
        var weekStart = date.AddDays(-(((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7));
        var isoWeek = ISOWeek.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue));
        var start = isoWeek % 2 == 0 ? weekStart.AddDays(-7) : weekStart;
        return (start, start.AddDays(13));
    }

    [Test]
    public void Anchor_IsMondayOfIsoWeekOneOf2026()
    {
        BiweeklyPayPeriod.Anchor.ShouldBe(new DateOnly(2025, 12, 29));
        BiweeklyPayPeriod.Anchor.DayOfWeek.ShouldBe(DayOfWeek.Monday);
    }

    [Test]
    public void MondayStart_MatchesTheFormerIsoParityRule_OnEveryDayFrom2021_01_04To2027_01_03()
    {
        var mismatches = new List<string>();
        var checkedDays = 0;
        for (var day = EquivalenceFirstDay; day <= EquivalenceLastDay; day = day.AddDays(1))
        {
            checkedDays++;
            var expected = FormerIsoParityRule(day);
            var actual = BiweeklyPayPeriod.Containing(day, DayOfWeek.Monday);
            if (actual != expected)
            {
                mismatches.Add($"{day:yyyy-MM-dd}: expected {expected.Start:yyyy-MM-dd}..{expected.End:yyyy-MM-dd}, got {actual.Start:yyyy-MM-dd}..{actual.End:yyyy-MM-dd}");
            }
        }

        checkedDays.ShouldBe(EquivalenceLastDay.DayNumber - EquivalenceFirstDay.DayNumber + 1);
        mismatches.ShouldBeEmpty();
    }

    [TestCase("2020-12-28")]
    [TestCase("2027-01-04")]
    public void MondayStart_DivergesFromTheFormerIsoParityRule_RightOutsideTheEquivalenceRange(string isoDate)
    {
        var day = D(isoDate);

        BiweeklyPayPeriod.Containing(day, DayOfWeek.Monday).ShouldNotBe(FormerIsoParityRule(day));
    }

    [TestCase("2025-07-15", "2025-07-14", "2025-07-27")]
    [TestCase("2026-12-21", "2026-12-14", "2026-12-27")]
    [TestCase("2026-12-28", "2026-12-28", "2027-01-10")]
    [TestCase("2027-01-04", "2026-12-28", "2027-01-10")]
    [TestCase("2027-01-10", "2026-12-28", "2027-01-10")]
    [TestCase("2027-01-11", "2027-01-11", "2027-01-24")]
    public void MondayStart_FixedDates(string date, string expectedStart, string expectedEnd)
    {
        var (start, end) = BiweeklyPayPeriod.Containing(D(date), DayOfWeek.Monday);

        start.ShouldBe(D(expectedStart));
        end.ShouldBe(D(expectedEnd));
    }

    [TestCase("2025-07-15", "2025-07-13", "2025-07-26")]
    [TestCase("2026-12-21", "2026-12-13", "2026-12-26")]
    [TestCase("2026-12-28", "2026-12-27", "2027-01-09")]
    [TestCase("2027-01-04", "2026-12-27", "2027-01-09")]
    [TestCase("2027-01-11", "2027-01-10", "2027-01-23")]
    public void SundayStart_FixedDates(string date, string expectedStart, string expectedEnd)
    {
        var (start, end) = BiweeklyPayPeriod.Containing(D(date), DayOfWeek.Sunday);

        start.ShouldBe(D(expectedStart));
        end.ShouldBe(D(expectedEnd));
    }

    [TestCase(DayOfWeek.Monday)]
    [TestCase(DayOfWeek.Sunday)]
    [TestCase(DayOfWeek.Saturday)]
    public void ConsecutivePeriods_AreContiguous_NonOverlapping_AndStartOnTheConfiguredDay(DayOfWeek weekStartDay)
    {
        var first = new DateOnly(2024, 12, 1);
        var last = new DateOnly(2028, 1, 31);
        var previous = BiweeklyPayPeriod.Containing(first, weekStartDay);

        for (var day = first; day <= last; day = day.AddDays(1))
        {
            var period = BiweeklyPayPeriod.Containing(day, weekStartDay);

            period.Start.ShouldBeLessThanOrEqualTo(day);
            period.End.ShouldBeGreaterThanOrEqualTo(day);
            period.Start.DayOfWeek.ShouldBe(weekStartDay);
            (period.End.DayNumber - period.Start.DayNumber).ShouldBe(13);
            if (period != previous)
            {
                period.Start.ShouldBe(previous.End.AddDays(1), $"gap or overlap before {period.Start:yyyy-MM-dd}");
                previous = period;
            }
        }
    }
}
