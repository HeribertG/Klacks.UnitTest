// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ScheduleRecurringTaskSkill time-zone resolution: with no explicit timeZoneId the
/// schedule uses the app owner's address country (globalCalendarCountry → IANA zone); an explicit
/// timeZoneId overrides it; the user context and a hard default act as fallbacks. Also covers the pure
/// CountryTimeZones map.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ScheduleRecurringTaskSkillTests
{
    private IScheduledTaskRepository _repository = null!;
    private ISkillRegistry _skillRegistry = null!;
    private ISkillRiskClassifier _riskClassifier = null!;
    private ISettingsReader _settingsReader = null!;
    private ScheduleRecurringTaskSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IScheduledTaskRepository>();
        _skillRegistry = Substitute.For<ISkillRegistry>();
        _riskClassifier = Substitute.For<ISkillRiskClassifier>();
        _settingsReader = Substitute.For<ISettingsReader>();
        _skill = new ScheduleRecurringTaskSkill(_repository, _skillRegistry, _riskClassifier, _settingsReader);
    }

    private void OwnerCountry(string? code) =>
        _settingsReader.GetSetting(SettingKeys.GlobalCalendarCountry)
            .Returns(code is null ? (SettingsModel?)null : new SettingsModel { Type = SettingKeys.GlobalCalendarCountry, Value = code });

    private static SkillExecutionContext Ctx(string? userTimezone = null) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>(),
        UserTimezone = userTimezone
    };

    private static Dictionary<string, object> ReminderParams(string? timeZoneId = null)
    {
        var p = new Dictionary<string, object>
        {
            ["name"] = "weekly check",
            ["cronExpression"] = "0 8 * * 1",
            ["actionType"] = "reminder",
            ["messageText"] = "check coverage",
            ["apply"] = true
        };
        if (timeZoneId is not null)
        {
            p["timeZoneId"] = timeZoneId;
        }

        return p;
    }

    [Test]
    public async Task NoTimeZone_UsesOwnerCountry_Switzerland_ZurichZone()
    {
        OwnerCountry("CH");

        var result = await _skill.ExecuteAsync(Ctx(), ReminderParams());

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t => t.TimeZoneId == "Europe/Zurich"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoTimeZone_UsesOwnerCountry_Germany_BerlinZone()
    {
        OwnerCountry("DE");

        var result = await _skill.ExecuteAsync(Ctx(), ReminderParams());

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t => t.TimeZoneId == "Europe/Berlin"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExplicitTimeZone_OverridesOwnerCountry()
    {
        OwnerCountry("CH");

        var result = await _skill.ExecuteAsync(Ctx(), ReminderParams("America/New_York"));

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t => t.TimeZoneId == "America/New_York"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoOwnerCountry_FallsBackToUserTimezone()
    {
        OwnerCountry(null);

        var result = await _skill.ExecuteAsync(Ctx("Europe/Vienna"), ReminderParams());

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t => t.TimeZoneId == "Europe/Vienna"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownOwnerCountry_NoUserTimezone_FallsBackToDefault()
    {
        OwnerCountry("US");

        var result = await _skill.ExecuteAsync(Ctx(), ReminderParams());

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t => t.TimeZoneId == TimeZoneDefaults.DefaultTimezone), Arg.Any<CancellationToken>());
    }

    [TestCase("CH", "Europe/Zurich")]
    [TestCase("ch", "Europe/Zurich")]
    [TestCase(" DE ", "Europe/Berlin")]
    [TestCase("AT", "Europe/Vienna")]
    [TestCase("LI", "Europe/Vaduz")]
    public void CountryTimeZones_Resolve_KnownCodes(string code, string expected)
    {
        CountryTimeZones.Resolve(code).ShouldBe(expected);
    }

    [TestCase("US")]
    [TestCase("")]
    [TestCase(null)]
    public void CountryTimeZones_Resolve_UnknownOrEmpty_ReturnsNull(string? code)
    {
        CountryTimeZones.Resolve(code).ShouldBeNull();
    }
}
