// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for IsPaidOfficialHoliday, the question behind the holiday time surcharge: a date qualifies only when
/// an OFFICIAL entry on it is also PAID. Unofficial or reminder-only entries never qualify, even when paid; when
/// several official entries share the date, one paid official entry is enough.
/// </summary>

using Klacks.Api.Domain.Services.Holidays;

namespace Klacks.UnitTest.Services;

[TestFixture]
internal class HolidaysListCalculatorPaidHolidayTests
{
    private const int TestYear = 2026;
    private const string ChristmasRule = "12.25";

    private static readonly DateOnly Christmas = new(TestYear, 12, 25);

    [TestCase(true, true, true)]
    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(false, false, false)]
    public void IsPaidOfficialHoliday_SingleEntry_RequiresOfficialAndPaid(bool isMandatory, bool isPaid, bool expected)
    {
        var calculator = Build(CreateRule(isMandatory, isPaid));

        calculator.IsPaidOfficialHoliday(Christmas).ShouldBe(expected);
    }

    [Test]
    public void IsPaidOfficialHoliday_DateWithoutEntry_IsFalse()
    {
        var calculator = Build(CreateRule(isMandatory: true, isPaid: true));

        calculator.IsPaidOfficialHoliday(Christmas.AddDays(-1)).ShouldBeFalse();
    }

    [TestCase(true)]
    [TestCase(false)]
    public void IsPaidOfficialHoliday_OfficialPaidAndReminderOnSameDate_IsTrue(bool reminderFirst)
    {
        var official = CreateRule(isMandatory: true, isPaid: true);
        var reminder = CreateRule(isMandatory: false, isPaid: false);
        var calculator = reminderFirst ? Build(reminder, official) : Build(official, reminder);

        calculator.IsPaidOfficialHoliday(Christmas).ShouldBeTrue();
    }

    [TestCase(true)]
    [TestCase(false)]
    public void IsPaidOfficialHoliday_OfficialUnpaidAndPaidReminderOnSameDate_IsFalse(bool reminderFirst)
    {
        var official = CreateRule(isMandatory: true, isPaid: false);
        var reminder = CreateRule(isMandatory: false, isPaid: true);
        var calculator = reminderFirst ? Build(reminder, official) : Build(official, reminder);

        calculator.IsPaidOfficialHoliday(Christmas).ShouldBeFalse();
    }

    [TestCase(true)]
    [TestCase(false)]
    public void IsPaidOfficialHoliday_UnpaidAndPaidOfficialOnSameDate_IsTrue(bool unpaidFirst)
    {
        var unpaid = CreateRule(isMandatory: true, isPaid: false);
        var paid = CreateRule(isMandatory: true, isPaid: true);
        var calculator = unpaidFirst ? Build(unpaid, paid) : Build(paid, unpaid);

        calculator.IsPaidOfficialHoliday(Christmas).ShouldBeTrue();
    }

    [Test]
    public void ComputeHolidays_CarriesIsPaidIntoHolidayDate()
    {
        var calculator = Build(CreateRule(isMandatory: true, isPaid: true));

        calculator.GetHolidayInfo(Christmas)!.IsPaid.ShouldBeTrue();
    }

    [TestCase(true, true, true)]
    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(false, false, false)]
    public void HolidayDate_EarnsTimeSurcharge_OnlyWhenOfficialAndPaid(bool officially, bool isPaid, bool expected)
    {
        new HolidayDate { Officially = officially, IsPaid = isPaid }.EarnsTimeSurcharge.ShouldBe(expected);
    }

    private static HolidaysListCalculator Build(params CalendarRule[] rules)
    {
        var calculator = new HolidaysListCalculator { CurrentYear = TestYear };
        calculator.AddRange(rules);
        calculator.ComputeHolidays();
        return calculator;
    }

    private static CalendarRule CreateRule(bool isMandatory, bool isPaid) => new()
    {
        Id = Guid.NewGuid(),
        Country = "CH",
        State = string.Empty,
        Rule = ChristmasRule,
        SubRule = string.Empty,
        Name = new MultiLanguage { En = "Christmas Day" },
        IsMandatory = isMandatory,
        IsPaid = isPaid
    };
}
