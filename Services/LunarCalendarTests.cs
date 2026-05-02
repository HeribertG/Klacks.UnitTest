// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the Chinese lunisolar calendar (Lunar to Gregorian) conversion.
/// Reference dates verified against Hong Kong Observatory data.
/// </summary>

using Klacks.Api.Domain.Services.Holidays;

namespace Klacks.UnitTest.Services;

[TestFixture]
internal class LunarCalendarTests
{
    [TestCase(1, 1, 2024, 2024, 2, 10)]
    [TestCase(1, 1, 2025, 2025, 1, 29)]
    [TestCase(1, 1, 2026, 2026, 2, 17)]
    [TestCase(1, 1, 2027, 2027, 2, 6)]
    public void GetGregorianDateForLunarInYear_ChineseNewYear_ShouldMatchKnownDates(
        int lunarDay, int lunarMonth, int gregorianYear,
        int expectedYear, int expectedMonth, int expectedDay)
    {
        var result = LunarCalendar.GetGregorianDateForLunarInYear(lunarDay, lunarMonth, gregorianYear);

        result.ShouldBe(new DateOnly(expectedYear, expectedMonth, expectedDay));
    }

    [TestCase(5, 5, 2024, 2024, 6, 10)]
    [TestCase(15, 8, 2024, 2024, 9, 17)]
    public void GetGregorianDateForLunarInYear_DragonBoatAndMidAutumn_ShouldMatchKnownDates(
        int lunarDay, int lunarMonth, int gregorianYear,
        int expectedYear, int expectedMonth, int expectedDay)
    {
        var result = LunarCalendar.GetGregorianDateForLunarInYear(lunarDay, lunarMonth, gregorianYear);

        result.ShouldBe(new DateOnly(expectedYear, expectedMonth, expectedDay));
    }

    [Test]
    public void GetGregorianDateForLunarInYear_OutOfRange_ShouldThrow()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => LunarCalendar.GetGregorianDateForLunarInYear(1, 1, 2019));
    }

    [Test]
    public void GetGregorianDateForLunarInYear_OutOfRangeHigh_ShouldThrow()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => LunarCalendar.GetGregorianDateForLunarInYear(1, 1, 2051));
    }

    [TestCase(2020)]
    [TestCase(2030)]
    [TestCase(2040)]
    [TestCase(2050)]
    public void GetGregorianDateForLunarInYear_AllSupportedYears_ShouldNotThrow(int year)
    {
        Should.NotThrow(() => { LunarCalendar.GetGregorianDateForLunarInYear(1, 1, year); });
    }
}
