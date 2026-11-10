// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the seeded Belgian and Irish public holiday rules (CalendarRulesSeed) to the dates the authorities publish.
/// The rows are read from the SQL the seed really emits (SeededCalendarRules) and computed with the production HolidaysListCalculator, so a
/// wrong rule string, a lost row or a broken weekday sub rule fails here. Expected dates: Belgian law of 4 January 1974
/// (ten national holidays) and Irish Organisation of Working Time Act 1997, Schedule 2, plus S.I. No. 50/2022
/// (Organisation of Working Time (Covid-19 Commemoration) Regulations 2022), Regulations 4 and 5, for St. Brigid's Day:
/// first Monday in February from 2023, or 1 February when that is a Friday.
/// </summary>

using Klacks.Api.Domain.Services.Holidays;

namespace Klacks.UnitTest.Infrastructure.Persistence.Seed;

[TestFixture]
public class BelgiumIrelandHolidaySeedTests
{
    private const string Belgium = "BE";
    private const string Ireland = "IE";
    private const string StBrigidsDay = "St. Brigid's Day";

    [Test]
    public void Belgium_2026_HasTheTenStatutoryHolidays()
    {
        var dates = SeededCalendarRules.Compute(Belgium, 2026);

        dates.ShouldBe(new[]
        {
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 4, 6),
            new DateOnly(2026, 5, 1),
            new DateOnly(2026, 5, 14),
            new DateOnly(2026, 5, 25),
            new DateOnly(2026, 7, 21),
            new DateOnly(2026, 8, 15),
            new DateOnly(2026, 11, 1),
            new DateOnly(2026, 11, 11),
            new DateOnly(2026, 12, 25),
        });
    }

    [Test]
    public void Ireland_2026_HasTheTenPublicHolidays()
    {
        var dates = SeededCalendarRules.Compute(Ireland, 2026);

        dates.ShouldBe(new[]
        {
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 2, 2),
            new DateOnly(2026, 3, 17),
            new DateOnly(2026, 4, 6),
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 8, 3),
            new DateOnly(2026, 10, 26),
            new DateOnly(2026, 12, 25),
            new DateOnly(2026, 12, 26),
        });
    }

    [TestCase(2023, 2, 6, TestName = "St. Brigid's Day 2023: first year, 1 February is a Wednesday, next Monday")]
    [TestCase(2024, 2, 5, TestName = "St. Brigid's Day 2024: 1 February is a Thursday, next Monday")]
    [TestCase(2025, 2, 3, TestName = "St. Brigid's Day 2025: 1 February is a Saturday, next Monday")]
    [TestCase(2026, 2, 2, TestName = "St. Brigid's Day 2026: 1 February is a Sunday, next Monday")]
    [TestCase(2027, 2, 1, TestName = "St. Brigid's Day 2027: 1 February is a Monday")]
    [TestCase(2030, 2, 1, TestName = "St. Brigid's Day 2030: 1 February is a Friday and stays")]
    [TestCase(2033, 2, 7, TestName = "St. Brigid's Day 2033: 1 February is a Tuesday, next Monday")]
    public void Ireland_StBrigidsDay_FollowsTheFridayException(int year, int month, int day)
    {
        var rules = SeededCalendarRules.Load(Ireland).Where(rule => rule.Name.En == StBrigidsDay).ToList();
        rules.Count.ShouldBe(1);

        var calculator = new HolidaysListCalculator { CurrentYear = year };
        calculator.AddRange(rules);
        calculator.ComputeHolidays();

        calculator.HolidayList.Single().CurrentDate.ShouldBe(new DateOnly(year, month, day));
    }

    [TestCase(Belgium)]
    [TestCase(Ireland)]
    public void SeededRules_AreNationalMandatoryAndPaid(string country)
    {
        var rules = SeededCalendarRules.Load(country);

        rules.Count.ShouldBe(10);
        rules.ShouldAllBe(rule => rule.State == country && rule.IsMandatory && rule.IsPaid);
    }
}
