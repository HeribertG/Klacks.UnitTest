// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Infrastructure.Services;

using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsConstants = Klacks.Api.Application.Constants.Settings;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

[TestFixture]
public class CompanyLocationProviderTests
{
    private ISettingsReader _settingsReader = null!;
    private IGeocodingService _geocodingService = null!;
    private IMemoryCache _cache = null!;
    private CompanyLocationProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _settingsReader = Substitute.For<ISettingsReader>();
        _geocodingService = Substitute.For<IGeocodingService>();
        _cache = new MemoryCache(new MemoryCacheOptions());
        _provider = new CompanyLocationProvider(
            _settingsReader,
            _geocodingService,
            _cache,
            NullLogger<CompanyLocationProvider>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
    }

    [Test]
    public async Task GetCompanyLocationAsync_NoZipAndNoPlace_ReturnsNullAndDoesNotGeocode()
    {
        _settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsModel?)null);

        var result = await _provider.GetCompanyLocationAsync();

        result.ShouldBeNull();
        await _geocodingService.DidNotReceive().GeocodeAddressAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetCompanyLocationAsync_WithFullAddress_ReturnsCoordinatesAndBuildsExpectedQuery()
    {
        SetupAddress(street: "Bahnhofstrasse 1", zip: "8001", place: "Zürich", country: "Schweiz");
        _geocodingService
            .GeocodeAddressAsync("Bahnhofstrasse 1, 8001 Zürich", "Schweiz", Arg.Any<CancellationToken>())
            .Returns(((double?)47.3769, (double?)8.5472));

        var result = await _provider.GetCompanyLocationAsync();

        result.ShouldNotBeNull();
        result!.Value.Latitude.ShouldBe(47.3769);
        result.Value.Longitude.ShouldBe(8.5472);
    }

    [Test]
    public async Task GetCompanyLocationAsync_OnlyPlace_BuildsLocalityOnlyQuery()
    {
        SetupAddress(street: string.Empty, zip: string.Empty, place: "Bern", country: "Schweiz");
        _geocodingService
            .GeocodeAddressAsync("Bern", "Schweiz", Arg.Any<CancellationToken>())
            .Returns(((double?)46.948, (double?)7.4474));

        var result = await _provider.GetCompanyLocationAsync();

        result.ShouldNotBeNull();
        await _geocodingService.Received(1).GeocodeAddressAsync("Bern", "Schweiz", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetCompanyLocationAsync_GeocodingReturnsNoCoordinates_ReturnsNull()
    {
        SetupAddress(street: "Bahnhofstrasse 1", zip: "8001", place: "Zürich", country: "Schweiz");
        _geocodingService
            .GeocodeAddressAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(((double?)null, (double?)null));

        var result = await _provider.GetCompanyLocationAsync();

        result.ShouldBeNull();
    }

    [Test]
    public async Task GetCompanyLocationAsync_SecondCall_ServedFromCacheWithoutReadingSettingsOrGeocodingAgain()
    {
        SetupAddress(street: "Bahnhofstrasse 1", zip: "8001", place: "Zürich", country: "Schweiz");
        _geocodingService
            .GeocodeAddressAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(((double?)47.3769, (double?)8.5472));

        await _provider.GetCompanyLocationAsync();
        var second = await _provider.GetCompanyLocationAsync();

        second.ShouldNotBeNull();
        await _settingsReader.Received(1).GetSetting(SettingsConstants.APP_ADDRESS_PLACE);
        await _geocodingService.Received(1).GeocodeAddressAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetCompanyLocationAsync_NegativeResultIsCached_DoesNotGeocodeTwice()
    {
        SetupAddress(street: "Bahnhofstrasse 1", zip: "8001", place: "Zürich", country: "Schweiz");
        _geocodingService
            .GeocodeAddressAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(((double?)null, (double?)null));

        await _provider.GetCompanyLocationAsync();
        var second = await _provider.GetCompanyLocationAsync();

        second.ShouldBeNull();
        await _geocodingService.Received(1).GeocodeAddressAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private void SetupAddress(string street, string zip, string place, string country)
    {
        _settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsModel?)null);
        _settingsReader.GetSetting(SettingsConstants.APP_ADDRESS_ADDRESS).Returns(Setting(SettingsConstants.APP_ADDRESS_ADDRESS, street));
        _settingsReader.GetSetting(SettingsConstants.APP_ADDRESS_ZIP).Returns(Setting(SettingsConstants.APP_ADDRESS_ZIP, zip));
        _settingsReader.GetSetting(SettingsConstants.APP_ADDRESS_PLACE).Returns(Setting(SettingsConstants.APP_ADDRESS_PLACE, place));
        _settingsReader.GetSetting(SettingsConstants.APP_ADDRESS_COUNTRY).Returns(Setting(SettingsConstants.APP_ADDRESS_COUNTRY, country));
    }

    private static SettingsModel Setting(string type, string value)
    {
        return new SettingsModel { Type = type, Value = value };
    }
}
