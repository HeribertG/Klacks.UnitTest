// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests the surcharge RateMode/MinimumPerHour resolution (K19) in ClientContractDataProvider. Unlike
/// NightStart/NightEnd, RateMode and MinimumPerHour are settings-only (no SchedulingRule/Contract column),
/// so only the settings-vs-hard-default fallback is covered here.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.Associations;

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Associations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class ClientContractDataProviderRateModeTests
{
    private DataBaseContext _context = null!;
    private ClientContractDataProvider _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, httpContextAccessor);
        _sut = new ClientContractDataProvider(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task GetEffectiveContractDataAsync_NoSettings_DefaultsToMultiplierAndNoMinimum()
    {
        var result = await _sut.GetEffectiveContractDataAsync(Guid.NewGuid(), new DateOnly(2026, 7, 15));

        result.NightRateMode.ShouldBe(SurchargeRateMode.Multiplier);
        result.HolidayRateMode.ShouldBe(SurchargeRateMode.Multiplier);
        result.WE1RateMode.ShouldBe(SurchargeRateMode.Multiplier);
        result.WE2RateMode.ShouldBe(SurchargeRateMode.Multiplier);
        result.WE3RateMode.ShouldBe(SurchargeRateMode.Multiplier);
        result.NightMinimumPerHour.ShouldBeNull();
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_SettingsConfigureFixedPerHour_UsesConfiguredMode()
    {
        _context.Settings.Add(new Settings { Id = Guid.NewGuid(), Type = SettingKeys.SurchargeNightRateMode, Value = "fixedPerHour" });
        _context.Settings.Add(new Settings { Id = Guid.NewGuid(), Type = SettingKeys.NightRate, Value = "0.73" });
        await _context.SaveChangesAsync();

        var result = await _sut.GetEffectiveContractDataAsync(Guid.NewGuid(), new DateOnly(2026, 7, 15));

        result.NightRateMode.ShouldBe(SurchargeRateMode.FixedPerHour);
        result.NightRate.ShouldBe(0.73m);
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_SettingsConfigureFixedPerShift_UsesConfiguredMode()
    {
        _context.Settings.Add(new Settings { Id = Guid.NewGuid(), Type = SettingKeys.SurchargeNightRateMode, Value = "fixedPerShift" });
        await _context.SaveChangesAsync();

        var result = await _sut.GetEffectiveContractDataAsync(Guid.NewGuid(), new DateOnly(2026, 7, 15));

        result.NightRateMode.ShouldBe(SurchargeRateMode.FixedPerShift);
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_UnparseableRateMode_FallsBackToMultiplier()
    {
        _context.Settings.Add(new Settings { Id = Guid.NewGuid(), Type = SettingKeys.SurchargeNightRateMode, Value = "not-a-mode" });
        await _context.SaveChangesAsync();

        var result = await _sut.GetEffectiveContractDataAsync(Guid.NewGuid(), new DateOnly(2026, 7, 15));

        result.NightRateMode.ShouldBe(SurchargeRateMode.Multiplier);
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_DistinctRateModesPerKey_MapEachToItsOwnProperty()
    {
        _context.Settings.Add(new Settings { Id = Guid.NewGuid(), Type = SettingKeys.SurchargeNightRateMode, Value = "fixedPerHour" });
        _context.Settings.Add(new Settings { Id = Guid.NewGuid(), Type = SettingKeys.SurchargeHolidayRateMode, Value = "fixedPerShift" });
        _context.Settings.Add(new Settings { Id = Guid.NewGuid(), Type = SettingKeys.SurchargeWE2RateMode, Value = "fixedPerHour" });
        _context.Settings.Add(new Settings { Id = Guid.NewGuid(), Type = SettingKeys.SurchargeWE3RateMode, Value = "fixedPerShift" });
        await _context.SaveChangesAsync();

        var result = await _sut.GetEffectiveContractDataAsync(Guid.NewGuid(), new DateOnly(2026, 7, 15));

        result.NightRateMode.ShouldBe(SurchargeRateMode.FixedPerHour);
        result.HolidayRateMode.ShouldBe(SurchargeRateMode.FixedPerShift);
        result.WE1RateMode.ShouldBe(SurchargeRateMode.Multiplier);
        result.WE2RateMode.ShouldBe(SurchargeRateMode.FixedPerHour);
        result.WE3RateMode.ShouldBe(SurchargeRateMode.FixedPerShift);
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_MinimumPerHourConfigured_IsParsed()
    {
        _context.Settings.Add(new Settings { Id = Guid.NewGuid(), Type = SettingKeys.SurchargeWE1MinimumPerHour, Value = "75.0" });
        await _context.SaveChangesAsync();

        var result = await _sut.GetEffectiveContractDataAsync(Guid.NewGuid(), new DateOnly(2026, 7, 15));

        result.WE1MinimumPerHour.ShouldBe(75.0m);
    }
}
