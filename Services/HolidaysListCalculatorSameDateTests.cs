// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the same-date collision of a merged calendar selection: when an official holiday and a
/// reminder-only holiday (OfficialOverride=false projected to IsMandatory=false) fall on the same date,
/// the official entry must always win - independent of the order in which the rules arrive and of the
/// list size (List.Sort switches from a stable insertion sort to an unstable introsort above 16 items), and
/// even when a caller reorders the public HolidayList afterwards. Within one date the list keeps rule order.
/// </summary>

using Klacks.Api.Domain.Services.Holidays;

namespace Klacks.UnitTest.Services;

[TestFixture]
internal class HolidaysListCalculatorSameDateTests
{
    private const int TestYear = 2026;
    private const string ChristmasRule = "12.25";
    private const string NewYearRule = "01.01";
    private const string OfficialHolidayName = "Weihnachten";
    private const string ReminderHolidayName = "Christmas Day";
    private const int FillerRuleCount = 40;
    private const int FillerDistinctDates = 5;
    private const string RuleDateFormat = "MM.dd";

    private static readonly DateOnly Christmas = new(TestYear, 12, 25);
    private static readonly DateOnly NewYear = new(TestYear, 1, 1);

    [TestCase(true, 0)]
    [TestCase(false, 0)]
    [TestCase(true, FillerRuleCount)]
    [TestCase(false, FillerRuleCount)]
    public void IsHoliday_OfficialAndReminderOnSameDate_ReturnsOfficial(bool reminderFirst, int fillerCount)
    {
        var calculator = BuildCalculator(reminderFirst, fillerCount);

        calculator.IsHoliday(Christmas).ShouldBe(HolidayStatus.OfficialHoliday);
        calculator.IsHoliday(NewYear).ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [TestCase(true, 0)]
    [TestCase(false, 0)]
    [TestCase(true, FillerRuleCount)]
    [TestCase(false, FillerRuleCount)]
    public void GetHolidayInfo_OfficialAndReminderOnSameDate_ReturnsOfficialEntry(bool reminderFirst, int fillerCount)
    {
        var calculator = BuildCalculator(reminderFirst, fillerCount);

        var info = calculator.GetHolidayInfo(Christmas);

        info.ShouldNotBeNull();
        info.Officially.ShouldBeTrue();
        info.Name.En.ShouldBe(OfficialHolidayName);
    }

    [TestCase(true, 0)]
    [TestCase(false, 0)]
    [TestCase(true, FillerRuleCount)]
    [TestCase(false, FillerRuleCount)]
    public void ComputeHolidays_OfficialAndReminderOnSameDate_KeepsRuleOrderWithinDate(bool reminderFirst, int fillerCount)
    {
        var calculator = BuildCalculator(reminderFirst, fillerCount);

        var sameDate = calculator.HolidayList.Where(h => h.CurrentDate == Christmas).ToList();

        sameDate.Count.ShouldBe(2);
        sameDate[0].Officially.ShouldBe(!reminderFirst);
        calculator.HolidayList.Select(h => h.CurrentDate).ShouldBe(calculator.HolidayList.Select(h => h.CurrentDate).Order());
    }

    [TestCase(true)]
    [TestCase(false)]
    public void GetHolidayInfo_PublicListReorderedExternally_StillReturnsOfficialEntry(bool reminderFirst)
    {
        var calculator = BuildCalculator(reminderFirst, fillerCount: 0);

        calculator.HolidayList.Reverse();

        calculator.GetHolidayInfo(Christmas)!.Officially.ShouldBeTrue();
        calculator.IsHoliday(Christmas).ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void IsHoliday_OnlyReminderEntriesOnDate_ReturnsUnofficial()
    {
        var calculator = new HolidaysListCalculator { CurrentYear = TestYear };
        calculator.Add(CreateRule(ChristmasRule, ReminderHolidayName, isMandatory: false));
        calculator.Add(CreateRule(ChristmasRule, OfficialHolidayName, isMandatory: false));
        calculator.ComputeHolidays();

        calculator.IsHoliday(Christmas).ShouldBe(HolidayStatus.UnofficialHoliday);
        calculator.GetHolidayInfo(Christmas)!.Name.En.ShouldBe(ReminderHolidayName);
    }

    [Test]
    public void IsHoliday_DateWithoutEntry_ReturnsNotAHoliday()
    {
        var calculator = BuildCalculator(reminderFirst: true, fillerCount: 0);

        calculator.IsHoliday(new DateOnly(TestYear, 3, 3)).ShouldBe(HolidayStatus.NotAHoliday);
        calculator.GetHolidayInfo(new DateOnly(TestYear, 3, 3)).ShouldBeNull();
    }

    private static HolidaysListCalculator BuildCalculator(bool reminderFirst, int fillerCount)
    {
        var calculator = new HolidaysListCalculator { CurrentYear = TestYear };
        var official = new[]
        {
            CreateRule(ChristmasRule, OfficialHolidayName, isMandatory: true),
            CreateRule(NewYearRule, OfficialHolidayName, isMandatory: true)
        };
        var reminder = new[]
        {
            CreateRule(ChristmasRule, ReminderHolidayName, isMandatory: false),
            CreateRule(NewYearRule, ReminderHolidayName, isMandatory: false)
        };

        calculator.AddRange(CreateFillerRules(fillerCount));
        calculator.AddRange(reminderFirst ? reminder : official);
        calculator.AddRange(reminderFirst ? official : reminder);
        calculator.AddRange(CreateFillerRules(fillerCount));
        calculator.ComputeHolidays();
        return calculator;
    }

    private static IEnumerable<CalendarRule> CreateFillerRules(int count)
    {
        var firstFillerDate = new DateOnly(TestYear, 2, 1);
        for (var index = 0; index < count; index++)
        {
            var fillerDate = firstFillerDate.AddDays(index % FillerDistinctDates);
            yield return CreateRule(fillerDate.ToString(RuleDateFormat), $"Filler {index}", isMandatory: index % 2 == 0);
        }
    }

    private static CalendarRule CreateRule(string rule, string name, bool isMandatory)
    {
        return new CalendarRule
        {
            Id = Guid.NewGuid(),
            Country = "CH",
            State = string.Empty,
            Rule = rule,
            SubRule = string.Empty,
            Name = new MultiLanguage { En = name },
            IsMandatory = isMandatory,
            IsPaid = true
        };
    }
}
