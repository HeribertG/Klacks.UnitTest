// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the seeded UK bank holidays (England and Wales, CalendarRulesSeed) to the dates gov.uk publishes. A bank
/// holiday on a weekend moves to the next free weekday and never back to Friday; Christmas and Boxing Day chain
/// (Christmas on Saturday -> Monday 27, Boxing Day on Sunday -> Tuesday 28). One-off holidays (2022 jubilee, state
/// funeral) and the moved 2022 spring holiday are proclamations, not rules, and are left out.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Persistence.Seed;

[TestFixture]
public class GreatBritainHolidaySeedTests
{
    private const string GreatBritain = "GB";
    private const string NewYearsDay = "New Year's Day";
    private const string ChristmasDay = "Christmas Day";
    private const string BoxingDay = "Boxing Day";

    [Test]
    public void GreatBritain_2027_HasTheEightBankHolidays()
    {
        var dates = SeededCalendarRules.Compute(GreatBritain, 2027);

        dates.ShouldBe(new[]
        {
            new DateOnly(2027, 1, 1),
            new DateOnly(2027, 3, 26),
            new DateOnly(2027, 3, 29),
            new DateOnly(2027, 5, 3),
            new DateOnly(2027, 5, 31),
            new DateOnly(2027, 8, 30),
            new DateOnly(2027, 12, 27),
            new DateOnly(2027, 12, 28),
        });
    }

    [TestCase(NewYearsDay, 2022, 1, 3, TestName = "New Year 2022: Saturday, substitute Monday 3 January")]
    [TestCase(NewYearsDay, 2023, 1, 2, TestName = "New Year 2023: Sunday, substitute Monday 2 January")]
    [TestCase(NewYearsDay, 2027, 1, 1, TestName = "New Year 2027: Friday, stays")]
    [TestCase(ChristmasDay, 2021, 12, 27, TestName = "Christmas 2021: Saturday, substitute Monday 27")]
    [TestCase(BoxingDay, 2021, 12, 28, TestName = "Boxing Day 2021: Sunday, substitute Tuesday 28")]
    [TestCase(ChristmasDay, 2022, 12, 27, TestName = "Christmas 2022: Sunday, substitute Tuesday 27")]
    [TestCase(BoxingDay, 2022, 12, 26, TestName = "Boxing Day 2022: Monday, stays")]
    [TestCase(ChristmasDay, 2026, 12, 25, TestName = "Christmas 2026: Friday, stays")]
    [TestCase(BoxingDay, 2026, 12, 28, TestName = "Boxing Day 2026: Saturday, substitute Monday 28")]
    public void WeekendBankHoliday_MovesToTheNextFreeWeekday(string holiday, int year, int month, int day)
    {
        var rules = SeededCalendarRules.Load(GreatBritain).Where(rule => rule.Name.En == holiday).ToList();
        rules.Count.ShouldBe(1);

        SeededCalendarRules.Compute(rules, year).ShouldBe(new[] { new DateOnly(year, month, day) });
    }

    [Test]
    public void SeededRules_AreNationalMandatoryAndPaid()
    {
        var rules = SeededCalendarRules.Load(GreatBritain);

        rules.Count.ShouldBe(8);
        rules.ShouldAllBe(rule => rule.State == GreatBritain && rule.IsMandatory && rule.IsPaid);
    }
}
