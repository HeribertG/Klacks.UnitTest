// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the Tabular Islamic Calendar (Hijri to Gregorian) conversion.
/// Reference dates verified against established Hijri-Gregorian conversion tables.
/// </summary>

using Klacks.Api.Domain.Services.Holidays;

namespace Klacks.UnitTest.Services;

[TestFixture]
internal class HijriCalendarTests
{
    [TestCase(1445, 10, 1, 2024, 4, 10)]
    [TestCase(1445, 12, 10, 2024, 6, 17)]
    [TestCase(1446, 10, 1, 2025, 3, 31)]
    [TestCase(1446, 12, 10, 2025, 6, 7)]
    [TestCase(1447, 1, 1, 2025, 6, 27)]
    [TestCase(1447, 10, 1, 2026, 3, 20)]
    public void HijriToGregorian_ShouldConvertKnownDates(
        int hijriYear, int hijriMonth, int hijriDay,
        int expectedYear, int expectedMonth, int expectedDay)
    {
        var result = HijriCalendar.HijriToGregorian(hijriYear, hijriMonth, hijriDay);

        result.Year.Should().BeCloseTo(expectedYear, 0);
        result.Month.Should().BeCloseTo(expectedMonth, 0);
        result.Day.Should().BeCloseTo(expectedDay, 2);
    }

    [TestCase(1, 10, 2024)]
    [TestCase(10, 12, 2024)]
    [TestCase(1, 10, 2025)]
    [TestCase(1, 1, 2026)]
    public void GetGregorianDateForHijriInYear_ShouldReturnDateInRequestedYear(
        int hijriDay, int hijriMonth, int gregorianYear)
    {
        var result = HijriCalendar.GetGregorianDateForHijriInYear(hijriDay, hijriMonth, gregorianYear);

        result.Year.Should().Be(gregorianYear);
    }

    [Test]
    public void GregorianToHijri_ShouldRoundTrip()
    {
        var original = new DateOnly(2026, 3, 20);
        var (hijriYear, hijriMonth, hijriDay) = HijriCalendar.GregorianToHijri(original);
        var roundTripped = HijriCalendar.HijriToGregorian(hijriYear, hijriMonth, hijriDay);

        roundTripped.Should().Be(original);
    }

    [TestCase(2024)]
    [TestCase(2025)]
    [TestCase(2026)]
    [TestCase(2030)]
    public void GetGregorianDateForHijriInYear_EidAlFitr_ShouldBeInFirstHalfOfYear(int year)
    {
        var result = HijriCalendar.GetGregorianDateForHijriInYear(1, 10, year);

        result.Year.Should().Be(year);
    }
}
