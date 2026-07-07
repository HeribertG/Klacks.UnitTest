// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Infrastructure.Services;

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Infrastructure.Services;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

[TestFixture]
public class WeekConfigurationTests
{
    private ISettingsReader _settingsReader = null!;
    private WeekConfiguration _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _settingsReader = Substitute.For<ISettingsReader>();
        _settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsModel?)null);
        _sut = new WeekConfiguration(_settingsReader);
    }

    [Test]
    public async Task GetWeekendDaysAsync_Unconfigured_FallsBackToSaturdayAndSunday()
    {
        var weekendDays = await _sut.GetWeekendDaysAsync();

        weekendDays.ShouldBe(new[] { DayOfWeek.Saturday, DayOfWeek.Sunday }, ignoreOrder: true);
    }

    [Test]
    public async Task GetWeekendDaysAsync_ConfiguredForGulfCluster_ReturnsFridayAndSaturday()
    {
        SetSetting(SettingKeys.WeekendDays, "Friday,Saturday");

        var weekendDays = await _sut.GetWeekendDaysAsync();

        weekendDays.ShouldBe(new[] { DayOfWeek.Friday, DayOfWeek.Saturday }, ignoreOrder: true);
    }

    [Test]
    public async Task IsWeekendAsync_FridayConfiguredAsWeekend_ReturnsTrueForFridayAndFalseForSaturday()
    {
        SetSetting(SettingKeys.WeekendDays, "Friday,Saturday");

        (await _sut.IsWeekendAsync(DayOfWeek.Friday)).ShouldBeTrue();
        (await _sut.IsWeekendAsync(DayOfWeek.Sunday)).ShouldBeFalse();
    }

    [Test]
    public async Task GetWeekStartDayAsync_Unconfigured_FallsBackToMonday()
    {
        var weekStartDay = await _sut.GetWeekStartDayAsync();

        weekStartDay.ShouldBe(DayOfWeek.Monday);
    }

    [Test]
    public async Task GetWeekStartDayAsync_ConfiguredAsSunday_ReturnsSunday()
    {
        SetSetting(SettingKeys.WeekStartDay, "Sunday");

        var weekStartDay = await _sut.GetWeekStartDayAsync();

        weekStartDay.ShouldBe(DayOfWeek.Sunday);
    }

    [TestCase("2026-07-06", "2026-07-06")] // Monday, week start default (Monday)
    [TestCase("2026-07-08", "2026-07-06")] // Wednesday -> Monday
    [TestCase("2026-07-12", "2026-07-06")] // Sunday -> previous Monday
    public async Task GetWeekStartAsync_DefaultMondayStart_ReturnsMondayOfThatWeek(string date, string expected)
    {
        var weekStart = await _sut.GetWeekStartAsync(DateOnly.Parse(date));

        weekStart.ShouldBe(DateOnly.Parse(expected));
    }

    [TestCase("2026-07-05", "2026-07-05")] // Sunday, week start Sunday
    [TestCase("2026-07-08", "2026-07-05")] // Wednesday -> previous Sunday
    [TestCase("2026-07-11", "2026-07-05")] // Saturday -> previous Sunday
    public async Task GetWeekStartAsync_ConfiguredSundayStart_ReturnsSundayOfThatWeek(string date, string expected)
    {
        SetSetting(SettingKeys.WeekStartDay, "Sunday");

        var weekStart = await _sut.GetWeekStartAsync(DateOnly.Parse(date));

        weekStart.ShouldBe(DateOnly.Parse(expected));
    }

    private void SetSetting(string type, string value)
    {
        _settingsReader.GetSetting(type).Returns(new SettingsModel { Type = type, Value = value });
    }
}
