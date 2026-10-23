// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for SporadicRangeCalculator: pin the blocking range of every sporadic scope so a
/// refactoring (e.g. naming the calendar literals) cannot shift a boundary by a day.
/// </summary>

using System.Globalization;
using Klacks.Api.Domain.Services.Shifts;

namespace Klacks.UnitTest.Domain.Services.Shifts;

[TestFixture]
public class SporadicRangeCalculatorTests
{
    private static SporadicShiftInfo Shift(ShiftSporadic scope, DateOnly? from = null, DateOnly? until = null) =>
        new("Sporadic", true, scope, from ?? new DateOnly(2026, 1, 1), until, 1, 1);

    [TestCase("2026-10-19", "2026-10-19", "2026-10-25")]
    [TestCase("2026-10-23", "2026-10-19", "2026-10-25")]
    [TestCase("2026-10-25", "2026-10-19", "2026-10-25")]
    [TestCase("2026-01-01", "2025-12-29", "2026-01-04")]
    public void Week_SpansMondayToSunday(string booking, string expectedFrom, string expectedUntil)
    {
        AssertRange(ShiftSporadic.Week, booking, expectedFrom, expectedUntil);
    }

    [TestCase("2026-02-14", "2026-02-01", "2026-02-28")]
    [TestCase("2028-02-29", "2028-02-01", "2028-02-29")]
    [TestCase("2026-12-31", "2026-12-01", "2026-12-31")]
    public void Month_SpansTheCalendarMonth(string booking, string expectedFrom, string expectedUntil)
    {
        AssertRange(ShiftSporadic.Month, booking, expectedFrom, expectedUntil);
    }

    [TestCase("2026-01-01", "2026-01-01", "2026-03-31")]
    [TestCase("2026-03-31", "2026-01-01", "2026-03-31")]
    [TestCase("2026-04-01", "2026-04-01", "2026-06-30")]
    [TestCase("2026-08-15", "2026-07-01", "2026-09-30")]
    [TestCase("2026-12-31", "2026-10-01", "2026-12-31")]
    public void Quarter_SpansTheCalendarQuarter(string booking, string expectedFrom, string expectedUntil)
    {
        AssertRange(ShiftSporadic.Quarter, booking, expectedFrom, expectedUntil);
    }

    [TestCase("2026-01-01", "2026-01-01", "2026-06-30")]
    [TestCase("2026-06-30", "2026-01-01", "2026-06-30")]
    [TestCase("2026-07-01", "2026-07-01", "2026-12-31")]
    [TestCase("2026-12-31", "2026-07-01", "2026-12-31")]
    public void Semester_SpansTheHalfYear(string booking, string expectedFrom, string expectedUntil)
    {
        AssertRange(ShiftSporadic.Semester, booking, expectedFrom, expectedUntil);
    }

    [TestCase("2026-01-01", "2026-01-01", "2026-12-31")]
    [TestCase("2026-10-23", "2026-01-01", "2026-12-31")]
    [TestCase("9999-12-31", "9999-01-01", "9999-12-31")]
    public void Year_SpansTheCalendarYear(string booking, string expectedFrom, string expectedUntil)
    {
        AssertRange(ShiftSporadic.Year, booking, expectedFrom, expectedUntil);
    }

    [Test]
    public void ContractualTerm_UsesTheShiftBounds()
    {
        var shift = Shift(ShiftSporadic.ContractualTerm, new DateOnly(2026, 3, 1), new DateOnly(2026, 9, 30));

        var (from, until) = SporadicRangeCalculator.Compute(shift, new DateOnly(2026, 5, 5));

        from.ShouldBe(new DateOnly(2026, 3, 1));
        until.ShouldBe(new DateOnly(2026, 9, 30));
    }

    [Test]
    public void ContractualTerm_WithoutEnd_FallsBackToTheLastRepresentableDay()
    {
        var shift = Shift(ShiftSporadic.ContractualTerm, new DateOnly(2026, 3, 1));

        var (_, until) = SporadicRangeCalculator.Compute(shift, new DateOnly(2026, 5, 5));

        until.ShouldBe(new DateOnly(9999, 12, 31));
    }

    private static void AssertRange(ShiftSporadic scope, string booking, string expectedFrom, string expectedUntil)
    {
        var (from, until) = SporadicRangeCalculator.Compute(Shift(scope), DateOnly.Parse(booking, CultureInfo.InvariantCulture, DateTimeStyles.None));

        from.ShouldBe(DateOnly.Parse(expectedFrom, CultureInfo.InvariantCulture, DateTimeStyles.None));
        until.ShouldBe(DateOnly.Parse(expectedUntil, CultureInfo.InvariantCulture, DateTimeStyles.None));
    }
}
