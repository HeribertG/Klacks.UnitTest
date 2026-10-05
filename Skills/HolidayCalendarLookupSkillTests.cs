// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Encodings.Web;
using System.Text.Json;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.CalendarSelections;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.CalendarSelections;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Services.Holidays;

namespace Klacks.UnitTest.Skills;

/// <summary>
/// list_holidays_for_period and validate_holiday_overlap answer from the calendar that really decides: a named
/// calendar selection and the company calendar come from IClientHolidayCalendarResolver (so a "reminder only"
/// entry is not official there), a bare country/region lists national plus regional entries the way a seeded
/// selection does, and every holiday says whether working on it earns the holiday time surcharge (official AND
/// marked for the time surcharge).
/// </summary>
[TestFixture]
public class HolidayCalendarLookupSkillTests
{
    private const int Year = 2026;
    private static readonly JsonSerializerOptions RelaxedJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private ICalendarSelectionRepository _calendarSelectionRepository = null!;
    private IClientHolidayCalendarResolver _holidayCalendarResolver = null!;
    private IHolidayCalendarSourceResolver _holidayCalendarSourceResolver = null!;
    private ISettingsRepository _settingsRepository = null!;
    private IHolidayCalendarLookupService _lookupService = null!;

    [SetUp]
    public void SetUp()
    {
        _calendarSelectionRepository = Substitute.For<ICalendarSelectionRepository>();
        _calendarSelectionRepository.List().Returns(new List<CalendarSelection>());
        _holidayCalendarResolver = Substitute.For<IClientHolidayCalendarResolver>();
        _holidayCalendarSourceResolver = Substitute.For<IHolidayCalendarSourceResolver>();
        _holidayCalendarSourceResolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedHolidayCalendarSource(HolidayCalendarSource.None, null, null, null, null));
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _settingsRepository.GetCalendarRuleList().Returns(new List<CalendarRule>());
        _lookupService = new HolidayCalendarLookupService(_settingsRepository);
    }

    [Test]
    public async Task List_CountryAndRegion_IncludesNationalAndRegionalButNoOtherRegion()
    {
        _settingsRepository.GetCalendarRuleList().Returns(new List<CalendarRule>
        {
            Rule("CH", "CH", "01.01", "Neujahr"),
            Rule("CH", "BE", "01.02", "Berchtoldstag"),
            Rule("CH", "TI", "03.19", "San Giuseppe"),
        });

        var result = await ListSkill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["country"] = "CH",
            ["state"] = "BE",
            ["fromDate"] = "2026-01-01",
            ["untilDate"] = "2026-12-31",
        });

        result.Success.ShouldBeTrue(result.Message);
        var json = Serialize(result.Data);
        json.ShouldContain("Neujahr");
        json.ShouldContain("Berchtoldstag");
        json.ShouldNotContain("San Giuseppe");
        result.Message.ShouldContain("2 holiday(s)");
    }

    [Test]
    public async Task List_RejectsInvertedRange()
    {
        var result = await ListSkill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["country"] = "CH",
            ["fromDate"] = "2026-12-31",
            ["untilDate"] = "2026-01-01",
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("on or after");
    }

    [Test]
    public async Task List_NamedSelection_UsesTheProductionResolverWithItsId()
    {
        var selection = new CalendarSelection { Id = Guid.NewGuid(), Name = "Bern + USA" };
        _calendarSelectionRepository.List().Returns(new List<CalendarSelection> { selection });
        _holidayCalendarResolver.GetCalculatorAsync(selection.Id, Year)
            .Returns(Calculator(Rule("US", "US", "07.04", "Independence Day", isMandatory: false)));

        var result = await ListSkill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["calendarSelectionName"] = "Bern + USA",
            ["fromDate"] = "2026-07-01",
            ["untilDate"] = "2026-07-31",
        });

        result.Success.ShouldBeTrue(result.Message);
        var row = JsonSerializer.SerializeToElement(result.Data).GetProperty("Holidays")[0];
        row.GetProperty("Officially").GetBoolean().ShouldBeFalse();
        row.GetProperty("EarnsHolidayTimeSurchargeWhenWorked").GetBoolean().ShouldBeFalse();
        await _holidayCalendarResolver.Received().GetCalculatorAsync(selection.Id, Year);
    }

    [Test]
    public async Task List_UnknownSelection_ReturnsRealNamesInsteadOfGuessing()
    {
        _calendarSelectionRepository.List().Returns(new List<CalendarSelection> { new() { Id = Guid.NewGuid(), Name = "Kanton Zürich" } });

        var result = await ListSkill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["calendarSelectionName"] = "Mars",
            ["fromDate"] = "2026-01-01",
            ["untilDate"] = "2026-12-31",
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Kanton Zürich");
    }

    [Test]
    public async Task List_WithoutCalendarOrCountry_UsesTheCompanyCalendar()
    {
        _holidayCalendarSourceResolver.ResolveAsync(null, Arg.Any<CancellationToken>())
            .Returns(new ResolvedHolidayCalendarSource(HolidayCalendarSource.CompanyDefault, Guid.NewGuid(), "Kanton Bern", null, null));
        _holidayCalendarResolver.GetCalculatorAsync(null, Year).Returns(Calculator(Rule("CH", "CH", "08.01", "Bundesfeier")));

        var result = await ListSkill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["fromDate"] = "2026-08-01",
            ["untilDate"] = "2026-08-01",
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("Kanton Bern");
        Serialize(result.Data).ShouldContain("Bundesfeier");
    }

    [Test]
    public async Task List_PeriodLongerThanFiveYears_IsRefusedWithoutLoadingRules()
    {
        var result = await ListSkill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["country"] = "CH",
            ["fromDate"] = "2026-01-01",
            ["untilDate"] = "2031-01-02",
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("5 years");
        await _settingsRepository.DidNotReceive().GetCalendarRuleList();
    }

    [Test]
    public async Task List_CountryOverSeveralYears_ReadsTheRuleTableOnce()
    {
        _settingsRepository.GetCalendarRuleList().Returns(new List<CalendarRule> { Rule("CH", "CH", "01.01", "Neujahr") });

        var result = await ListSkill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["country"] = "CH",
            ["fromDate"] = "2026-01-01",
            ["untilDate"] = "2028-12-31",
        });

        result.Message.ShouldContain("3 holiday(s)");
        await _settingsRepository.Received(1).GetCalendarRuleList();
    }

        [TestCase("ja", "元日")]
    [TestCase("zh-CN", "元旦")]
    public async Task List_NamesTheHolidayInTheUserLanguage(string language, string expected)
    {
        var rule = Rule("CH", "CH", "01.01", "Neujahr");
        rule.Name.SetValue("ja", "元日");
        rule.Name.SetValue("zh-cn", "元旦");
        _settingsRepository.GetCalendarRuleList().Returns(new List<CalendarRule> { rule });

        var result = await ListSkill().ExecuteAsync(Ctx() with { UserLanguage = language }, new Dictionary<string, object>
        {
            ["country"] = "CH",
            ["fromDate"] = "2026-01-01",
            ["untilDate"] = "2026-01-31",
        });

        Serialize(result.Data).ShouldContain(expected);
    }

    [TestCase(true, true, true, true)]
    [TestCase(true, false, true, false)]
    [TestCase(false, true, false, false)]
    public async Task Validate_ReportsOfficialAndSurcharge(bool isMandatory, bool isPaid, bool official, bool surcharge)
    {
        _settingsRepository.GetCalendarRuleList().Returns(new List<CalendarRule>
        {
            Rule("CH", "CH", "12.25", "Weihnachten", isMandatory, isPaid),
        });

        var result = await ValidateSkill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["date"] = "2026-12-25",
            ["country"] = "CH",
        });

        result.Success.ShouldBeTrue(result.Message);
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("IsHoliday").GetBoolean().ShouldBeTrue();
        data.GetProperty("Officially").GetBoolean().ShouldBe(official);
        data.GetProperty("EarnsHolidayTimeSurchargeWhenWorked").GetBoolean().ShouldBe(surcharge);
    }

    [Test]
    public async Task Validate_NormalDay_IsNotAHoliday()
    {
        _settingsRepository.GetCalendarRuleList().Returns(new List<CalendarRule> { Rule("CH", "CH", "01.01", "Neujahr") });

        var result = await ValidateSkill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["date"] = "2026-03-15",
            ["country"] = "CH",
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("NOT a holiday");
    }

    [Test]
    public async Task Validate_NoCalendarConfigured_IsNotAHoliday()
    {
        _holidayCalendarResolver.GetCalculatorAsync(null, Year).Returns((IHolidaysListCalculator?)null);

        var result = await ValidateSkill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["date"] = "2026-12-25" });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("no holiday calendar configured");
    }

    [TestCase("ja", "元日")]
    [TestCase("de", "Neujahr")]
    public async Task Validate_NamesTheHolidayInTheUserLanguage(string language, string expected)
    {
        var rule = Rule("CH", "CH", "01.01", "Neujahr");
        rule.Name.SetValue("ja", "元日");
        _settingsRepository.GetCalendarRuleList().Returns(new List<CalendarRule> { rule });

        var result = await ValidateSkill().ExecuteAsync(Ctx() with { UserLanguage = language }, new Dictionary<string, object>
        {
            ["date"] = "2026-01-01",
            ["country"] = "CH",
        });

        result.Message.ShouldContain(expected);
    }

    private HolidayCalendarTargetResolver TargetResolver() => new(
        _calendarSelectionRepository, _holidayCalendarResolver, _holidayCalendarSourceResolver, _lookupService);

    private ListHolidaysForPeriodSkill ListSkill() => new(TargetResolver());

    private ValidateHolidayOverlapSkill ValidateSkill() => new(TargetResolver());

    private static IHolidaysListCalculator Calculator(params CalendarRule[] rules)
    {
        var calculator = new HolidaysListCalculator { CurrentYear = Year };
        calculator.AddRange(rules);
        calculator.ComputeHolidays();
        return calculator;
    }

    private static CalendarRule Rule(string country, string state, string rule, string name, bool isMandatory = true, bool isPaid = true)
    {
        var multiLanguage = new MultiLanguage();
        multiLanguage.SetValue("de", name);
        multiLanguage.SetValue("en", name);
        return new CalendarRule
        {
            Id = Guid.NewGuid(),
            Country = country,
            State = state,
            Rule = rule,
            SubRule = string.Empty,
            Name = multiLanguage,
            Description = MultiLanguage.Empty(),
            IsMandatory = isMandatory,
            IsPaid = isPaid,
        };
    }

    private static string Serialize(object? data) => JsonSerializer.Serialize(data, RelaxedJson);

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewSettings" },
    };
}
