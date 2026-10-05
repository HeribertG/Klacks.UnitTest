// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for import_calendar_rules. list_holidays_for_period and validate_holiday_overlap moved to
/// HolidayCalendarLookupSkillTests when they were rebuilt on the production calendar resolution.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Settings;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class HolidaySkillTests
{
    private ISettingsRepository _settingsRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ICompanyClock _companyClock = null!;
    private IHolidayCalculatorCache _holidayCache = null!;

    [SetUp]
    public void Setup()
    {
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _holidayCache = Substitute.For<IHolidayCalculatorCache>();
        _companyClock = new FixedCompanyClock(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero));
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditSettings", "CanViewSettings" }
    };

    private static CalendarRule NewYearRule()
    {
        var name = new MultiLanguage();
        name.SetValue("de", "Neujahr");
        name.SetValue("en", "Neujahr");
        return new CalendarRule
        {
            Id = Guid.NewGuid(),
            Country = "CH",
            State = string.Empty,
            Rule = "01.01",
            SubRule = string.Empty,
            Name = name,
            Description = MultiLanguage.Empty(),
            IsMandatory = true,
            IsPaid = true
        };
    }

    private static CalendarRule LaborDayRule()
    {
        var name = new MultiLanguage();
        name.SetValue("de", "Tag der Arbeit");
        return new CalendarRule
        {
            Id = Guid.NewGuid(),
            Country = "CH",
            State = "BE",
            Rule = "05.01",
            SubRule = string.Empty,
            Name = name,
            Description = MultiLanguage.Empty(),
            IsMandatory = true,
            IsPaid = true
        };
    }

    [Test]
    public async Task ImportCalendarRules_RejectsInvalidJson()
    {
        var skill = new ImportCalendarRulesSkill(_settingsRepository, _unitOfWork, _companyClock, _holidayCache);
        var parameters = new Dictionary<string, object>
        {
            ["country"] = "CH",
            ["rulesJson"] = "{ this is not valid json }"
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("valid JSON"));
        _settingsRepository.DidNotReceive().AddCalendarRule(Arg.Any<CalendarRule>());
    }

    [Test]
    public async Task ImportCalendarRules_RejectsEmptyArray()
    {
        var skill = new ImportCalendarRulesSkill(_settingsRepository, _unitOfWork, _companyClock, _holidayCache);
        var parameters = new Dictionary<string, object>
        {
            ["country"] = "CH",
            ["rulesJson"] = "[]"
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("no rules"));
    }

    [Test]
    public async Task ImportCalendarRules_AddsValidRulesAndPersists()
    {
        var skill = new ImportCalendarRulesSkill(_settingsRepository, _unitOfWork, _companyClock, _holidayCache);
        var rulesJson = """
            [
              { "rule": "01.01", "nameDe": "Neujahr", "nameEn": "New Year" },
              { "rule": "08.01", "nameDe": "Bundesfeier" }
            ]
            """;
        var parameters = new Dictionary<string, object>
        {
            ["country"] = "CH",
            ["state"] = "BE",
            ["rulesJson"] = rulesJson
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        _settingsRepository.Received(2).AddCalendarRule(Arg.Any<CalendarRule>());
        await _unitOfWork.Received(1).CompleteAsync();
        Received.InOrder(() =>
        {
            _unitOfWork.CompleteAsync();
            _holidayCache.InvalidateAll();
        });
    }

    [Test]
    public async Task ImportCalendarRules_RejectedBatch_DoesNotInvalidateHolidayCalculators()
    {
        var skill = new ImportCalendarRulesSkill(_settingsRepository, _unitOfWork, _companyClock, _holidayCache);
        var parameters = new Dictionary<string, object>
        {
            ["country"] = "CH",
            ["rulesJson"] = "{ this is not valid json }"
        };

        await skill.ExecuteAsync(Ctx(), parameters);

        _holidayCache.DidNotReceive().InvalidateAll();
    }
}
