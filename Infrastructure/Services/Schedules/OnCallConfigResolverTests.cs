// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for OnCallConfigResolver proving the admin-editable WORKTIME_ONCALL_* settings are
/// effective end-to-end through the database-backed resolver (the pure parser is covered by
/// OnCallConfigTests): absent rows yield the documented defaults, seeded rows are reflected in the
/// resolved config, editing the presence percent changes the resolved factor (A/B), and disabling the
/// feature zeroes every contribution despite configured percents.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class OnCallConfigResolverTests
{
    private const string TrueValue = "true";
    private const string FalseValue = "false";

    private DataBaseContext _context = null!;
    private OnCallConfigResolver _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _sut = new OnCallConfigResolver(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private void SeedSetting(string type, string value)
    {
        _context.Settings.Add(new SettingsModel { Id = Guid.NewGuid(), Type = type, Value = value });
        _context.SaveChanges();
    }

    private void UpdateSetting(string type, string value)
    {
        var row = _context.Settings.Single(s => s.Type == type);
        row.Value = value;
        _context.SaveChanges();
    }

    [Test]
    public async Task ResolveAsync_NoSettings_ReturnsDisabledDefaults()
    {
        var config = await _sut.ResolveAsync();

        config.Enabled.ShouldBeFalse();
        config.PresenceFactor.ShouldBe(1.0m);
        config.StandbyFactor.ShouldBe(0.0m);
        config.IncludeInPeriodCaps.ShouldBeFalse();
    }

    [Test]
    public async Task ResolveAsync_SeededSettings_ReflectsConfiguredValues()
    {
        SeedSetting(SettingKeys.WorktimeOnCallEnabled, TrueValue);
        SeedSetting(SettingKeys.WorktimeOnCallPresenceCountsPercent, "80");
        SeedSetting(SettingKeys.WorktimeOnCallStandbyCountsPercent, "25");
        SeedSetting(SettingKeys.WorktimeOnCallIncludeInPeriodCaps, TrueValue);

        var config = await _sut.ResolveAsync();

        config.Enabled.ShouldBeTrue();
        config.PresenceFactor.ShouldBe(0.8m);
        config.StandbyFactor.ShouldBe(0.25m);
        config.IncludeInPeriodCaps.ShouldBeTrue();
    }

    [Test]
    public async Task ResolveAsync_PresencePercentEdited_YieldsDifferentFactor()
    {
        SeedSetting(SettingKeys.WorktimeOnCallEnabled, TrueValue);
        SeedSetting(SettingKeys.WorktimeOnCallPresenceCountsPercent, "100");

        var configA = await _sut.ResolveAsync();

        UpdateSetting(SettingKeys.WorktimeOnCallPresenceCountsPercent, "50");
        var configB = await _sut.ResolveAsync();

        configA.PresenceFactor.ShouldBe(1.0m);
        configB.PresenceFactor.ShouldBe(0.5m);
        configA.FactorFor(WorkChangeType.OnCallPresence).ShouldBe(1.0m);
        configB.FactorFor(WorkChangeType.OnCallPresence).ShouldBe(0.5m);
    }

    [Test]
    public async Task ResolveAsync_EnabledFalse_ZeroesEveryContributionDespiteConfiguredPercents()
    {
        SeedSetting(SettingKeys.WorktimeOnCallEnabled, FalseValue);
        SeedSetting(SettingKeys.WorktimeOnCallPresenceCountsPercent, "100");
        SeedSetting(SettingKeys.WorktimeOnCallStandbyCountsPercent, "50");

        var config = await _sut.ResolveAsync();

        config.Enabled.ShouldBeFalse();
        config.FactorFor(WorkChangeType.OnCallPresence).ShouldBe(0m);
        config.FactorFor(WorkChangeType.OnCallStandby).ShouldBe(0m);
    }
}
