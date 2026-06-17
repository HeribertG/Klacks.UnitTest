// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the get_weather skill: explicit-coordinate, city-geocoding and company-fallback
/// location paths, plus error handling when geocoding, the company location or Open-Meteo fail.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetWeatherSkillTests
{
    private IOpenMeteoClient _weatherClient = null!;
    private IGeocodingService _geocodingService = null!;
    private ICompanyLocationProvider _companyLocationProvider = null!;
    private GetWeatherSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _weatherClient = Substitute.For<IOpenMeteoClient>();
        _geocodingService = Substitute.For<IGeocodingService>();
        _companyLocationProvider = Substitute.For<ICompanyLocationProvider>();
        _skill = new GetWeatherSkill(_weatherClient, _geocodingService, _companyLocationProvider);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    private static WeatherSnapshot SampleSnapshot() => new()
    {
        TemperatureCelsius = 18.5,
        WindSpeedKmh = 12,
        WeatherCode = 2,
        Condition = "partly cloudy",
        Forecast = new List<WeatherDailyForecast>()
    };

    [Test]
    public async Task ExecuteAsync_WithExplicitCoordinates_ReturnsWeatherWithoutGeocoding()
    {
        _weatherClient.GetCurrentWeatherAsync(46.9, 7.4, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(SampleSnapshot());

        var parameters = new Dictionary<string, object>
        {
            ["latitude"] = 46.9,
            ["longitude"] = 7.4
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("18.5");
        await _geocodingService.DidNotReceiveWithAnyArgs().GeocodeAsync(default!, default!, default);
    }

    [Test]
    public async Task ExecuteAsync_WithJsonElementCoordinates_ParsesRealLlmFlow()
    {
        var parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(
            "{\"latitude\":46.9,\"longitude\":7.4}")!;
        _weatherClient.GetCurrentWeatherAsync(46.9, 7.4, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(SampleSnapshot());

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue();
        await _weatherClient.Received().GetCurrentWeatherAsync(46.9, 7.4, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_WithCity_GeocodesThenReturnsWeather()
    {
        (double? Latitude, double? Longitude) coords = (47.05, 8.31);
        _geocodingService.GeocodeAsync("Luzern", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(coords);
        _weatherClient.GetCurrentWeatherAsync(47.05, 8.31, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(SampleSnapshot());

        var parameters = new Dictionary<string, object> { ["city"] = "Luzern" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("Luzern");
    }

    [Test]
    public async Task ExecuteAsync_WithCity_GeocodeFails_ReturnsError()
    {
        (double? Latitude, double? Longitude) coords = (null, null);
        _geocodingService.GeocodeAsync("Nowhere", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(coords);

        var parameters = new Dictionary<string, object> { ["city"] = "Nowhere" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Nowhere");
        await _weatherClient.DidNotReceiveWithAnyArgs().GetCurrentWeatherAsync(default, default, default, default);
    }

    [Test]
    public async Task ExecuteAsync_NoLocation_UsesCompanyLocation()
    {
        (double Latitude, double Longitude)? company = (46.95, 7.44);
        _companyLocationProvider.GetCompanyLocationAsync(Arg.Any<CancellationToken>()).Returns(company);
        _weatherClient.GetCurrentWeatherAsync(46.95, 7.44, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(SampleSnapshot());

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        await _companyLocationProvider.Received().GetCompanyLocationAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_NoLocationAndNoCompanyLocation_ReturnsError()
    {
        (double Latitude, double Longitude)? company = null;
        _companyLocationProvider.GetCompanyLocationAsync(Arg.Any<CancellationToken>()).Returns(company);

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task ExecuteAsync_WeatherUnavailable_ReturnsError()
    {
        _weatherClient.GetCurrentWeatherAsync(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((WeatherSnapshot?)null);

        var parameters = new Dictionary<string, object>
        {
            ["latitude"] = 46.9,
            ["longitude"] = 7.4
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
    }
}
