// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Infrastructure.Services;

using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Infrastructure.Services;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsConstants = Klacks.Api.Application.Constants.Settings;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

[TestFixture]
public class CompanyClockTests
{
    private ISettingsReader _settingsReader = null!;

    [SetUp]
    public void SetUp()
    {
        _settingsReader = Substitute.For<ISettingsReader>();
        _settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsModel?)null);
    }

    [Test]
    public async Task GetTodayAsync_ExplicitTimeZoneAheadOfUtc_ReturnsLocalDateAsUtcMidnight()
    {
        SetSetting(SettingsConstants.APP_ADDRESS_TIMEZONE, "Asia/Tokyo");
        var clock = CreateClock("2026-06-27T23:30:00Z");

        var today = await clock.GetTodayAsync();

        today.ShouldBe(new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc));
        today.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Test]
    public async Task GetTodayAsync_NoTimeZoneButCountryConfigured_FallsBackToCountryZone()
    {
        SetSetting(SettingsConstants.APP_ADDRESS_COUNTRY, "CH");
        var clock = CreateClock("2026-06-27T22:30:00Z");

        var today = await clock.GetTodayAsync();

        today.ShouldBe(new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task GetTodayAsync_NoTimeZoneAndNoCountry_FallsBackToUtc()
    {
        var clock = CreateClock("2026-06-27T23:30:00Z");

        var today = await clock.GetTodayAsync();

        today.ShouldBe(new DateTime(2026, 6, 27, 0, 0, 0, DateTimeKind.Utc));
        today.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Test]
    public async Task GetTodayAsync_InvalidTimeZoneId_FallsThroughToCountryZone()
    {
        SetSetting(SettingsConstants.APP_ADDRESS_TIMEZONE, "Mars/Phobos");
        SetSetting(SettingsConstants.APP_ADDRESS_COUNTRY, "DE");
        var clock = CreateClock("2026-06-27T23:30:00Z");

        var today = await clock.GetTodayAsync();

        today.ShouldBe(new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc));
    }

    private void SetSetting(string type, string value)
    {
        _settingsReader.GetSetting(type).Returns(new SettingsModel { Type = type, Value = value });
    }

    private CompanyClock CreateClock(string utcInstant)
    {
        var timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(DateTimeOffset.Parse(
            utcInstant,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal));
        return new CompanyClock(_settingsReader, timeProvider);
    }
}
