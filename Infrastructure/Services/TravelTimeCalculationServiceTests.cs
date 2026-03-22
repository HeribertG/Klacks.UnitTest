// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for TravelTimeCalculationService: API key validation, coordinate resolution, and Haversine fallback.
/// </summary>
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using AppSettings = Klacks.Api.Application.Constants.Settings;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Infrastructure.Services;

[TestFixture]
public class TravelTimeCalculationServiceTests
{
    private ISettingsRepository _settingsRepository = null!;
    private ISettingsEncryptionService _encryptionService = null!;
    private IGeocodingService _geocodingService = null!;
    private MemoryCache _cache = null!;
    private IHttpClientFactory _httpClientFactory = null!;
    private ILogger<TravelTimeCalculationService> _logger = null!;
    private TravelTimeCalculationService _service = null!;

    [SetUp]
    public void Setup()
    {
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _encryptionService = Substitute.For<ISettingsEncryptionService>();
        _geocodingService = Substitute.For<IGeocodingService>();
        _cache = new MemoryCache(new MemoryCacheOptions());
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        _logger = Substitute.For<ILogger<TravelTimeCalculationService>>();

        _service = new TravelTimeCalculationService(
            _settingsRepository,
            _encryptionService,
            _geocodingService,
            _cache,
            _httpClientFactory,
            _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
    }

    [Test]
    public async Task IsApiKeyConfiguredAsync_NoSetting_ReturnsFalse()
    {
        // Arrange
        _settingsRepository.GetSetting(AppSettings.OPENROUTESERVICE_API_KEY)
            .Returns(Task.FromResult<SettingsEntity?>(null));

        // Act
        var result = await _service.IsApiKeyConfiguredAsync();

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public async Task IsApiKeyConfiguredAsync_EmptyValue_ReturnsFalse()
    {
        // Arrange
        var setting = new SettingsEntity
        {
            Type = AppSettings.OPENROUTESERVICE_API_KEY,
            Value = ""
        };
        _settingsRepository.GetSetting(AppSettings.OPENROUTESERVICE_API_KEY)
            .Returns(Task.FromResult<SettingsEntity?>(setting));

        // Act
        var result = await _service.IsApiKeyConfiguredAsync();

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public async Task IsApiKeyConfiguredAsync_ValidKey_ReturnsTrue()
    {
        // Arrange
        var setting = new SettingsEntity
        {
            Type = AppSettings.OPENROUTESERVICE_API_KEY,
            Value = "encrypted-key"
        };
        _settingsRepository.GetSetting(AppSettings.OPENROUTESERVICE_API_KEY)
            .Returns(Task.FromResult<SettingsEntity?>(setting));
        _encryptionService.ProcessForReading(AppSettings.OPENROUTESERVICE_API_KEY, "encrypted-key")
            .Returns("decrypted-api-key");

        // Act
        var result = await _service.IsApiKeyConfiguredAsync();

        // Assert
        result.Should().BeTrue();
    }

    [Test]
    public async Task IsApiKeyConfiguredAsync_CachesResult()
    {
        // Arrange
        _settingsRepository.GetSetting(AppSettings.OPENROUTESERVICE_API_KEY)
            .Returns(Task.FromResult<SettingsEntity?>(null));

        // Act
        await _service.IsApiKeyConfiguredAsync();
        await _service.IsApiKeyConfiguredAsync();

        // Assert
        await _settingsRepository.Received(1).GetSetting(AppSettings.OPENROUTESERVICE_API_KEY);
    }

    [Test]
    public async Task CalculateTravelTimeAsync_BothAddressesHaveCoordinates_ReturnsHaversineFallback()
    {
        // Arrange - Zurich to Bern (~120km)
        var from = new Address
        {
            Street = "Bahnhofstrasse 1",
            Zip = "8001",
            City = "Zürich",
            Country = "CH",
            Latitude = 47.3769,
            Longitude = 8.5417
        };
        var to = new Address
        {
            Street = "Bundesplatz 1",
            Zip = "3003",
            City = "Bern",
            Country = "CH",
            Latitude = 46.9480,
            Longitude = 7.4474
        };

        _settingsRepository.GetSetting(AppSettings.OPENROUTESERVICE_API_KEY)
            .Returns(Task.FromResult<SettingsEntity?>(null));

        // Act
        var result = await _service.CalculateTravelTimeAsync(from, to, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Value.TotalMinutes.Should().BeGreaterThan(60);
        result.Value.TotalMinutes.Should().BeLessThan(300);
    }

    [Test]
    public async Task CalculateTravelTimeAsync_NoCoordinatesNoGeocoding_ReturnsNull()
    {
        // Arrange
        var from = new Address { Street = "", Zip = "", City = "", Country = "" };
        var to = new Address { Street = "", Zip = "", City = "", Country = "" };

        // Act
        var result = await _service.CalculateTravelTimeAsync(from, to, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Test]
    public async Task CalculateTravelTimeAsync_GeocodingFallback_UsesGeocodingService()
    {
        // Arrange
        var from = new Address { Street = "Bahnhofstrasse 1", Zip = "8001", City = "Zürich", Country = "CH" };
        var to = new Address { Street = "Bundesplatz 1", Zip = "3003", City = "Bern", Country = "CH" };

        _geocodingService.GeocodeAddressAsync("Bahnhofstrasse 1, 8001 Zürich", "CH")
            .Returns((47.3769, 8.5417));
        _geocodingService.GeocodeAddressAsync("Bundesplatz 1, 3003 Bern", "CH")
            .Returns((46.9480, 7.4474));

        _settingsRepository.GetSetting(AppSettings.OPENROUTESERVICE_API_KEY)
            .Returns(Task.FromResult<SettingsEntity?>(null));

        // Act
        var result = await _service.CalculateTravelTimeAsync(from, to, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        await _geocodingService.Received(1).GeocodeAddressAsync("Bahnhofstrasse 1, 8001 Zürich", "CH");
        await _geocodingService.Received(1).GeocodeAddressAsync("Bundesplatz 1, 3003 Bern", "CH");
    }

    [Test]
    public async Task CalculateTravelTimeAsync_SameLocation_ReturnsZero()
    {
        // Arrange
        var addr = new Address
        {
            Street = "Bahnhofstrasse 1",
            Zip = "8001",
            City = "Zürich",
            Country = "CH",
            Latitude = 47.3769,
            Longitude = 8.5417
        };

        _settingsRepository.GetSetting(AppSettings.OPENROUTESERVICE_API_KEY)
            .Returns(Task.FromResult<SettingsEntity?>(null));

        // Act
        var result = await _service.CalculateTravelTimeAsync(addr, addr, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Value.TotalSeconds.Should().BeLessThan(1);
    }

    [Test]
    public async Task CalculateTravelTimeAsync_CachesTravelTime()
    {
        // Arrange
        var from = new Address { Latitude = 47.3769, Longitude = 8.5417, Country = "CH" };
        var to = new Address { Latitude = 46.9480, Longitude = 7.4474, Country = "CH" };

        _settingsRepository.GetSetting(AppSettings.OPENROUTESERVICE_API_KEY)
            .Returns(Task.FromResult<SettingsEntity?>(null));

        // Act
        var result1 = await _service.CalculateTravelTimeAsync(from, to, CancellationToken.None);
        var result2 = await _service.CalculateTravelTimeAsync(from, to, CancellationToken.None);

        // Assert
        result1.Should().Be(result2);
    }
}
