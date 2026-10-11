// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for DistanceMatrixBuilder: it must use the named routing HTTP client (the one carrying the
/// User-Agent the public OSRM server requires) and flag the matrix as estimated whenever the
/// Haversine fallback replaces the routing service.
/// </summary>

using System.Net;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.RouteOptimization;
using Microsoft.Extensions.Caching.Memory;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Services.RouteOptimization;

[TestFixture]
public class DistanceMatrixBuilderTests
{
    private const string OsrmTableJson = """
        {"code":"Ok","distances":[[0,2470],[2510,0]],"durations":[[0,144.2],[150.8,0]]}
        """;

    private StubHandler _handler = null!;
    private IHttpClientFactory _httpClientFactory = null!;
    private MemoryCache _cache = null!;
    private DistanceMatrixBuilder _builder = null!;

    private static readonly List<Location> TwoLocations =
    [
        new Location { Name = "A", Latitude = 47.4990, Longitude = 8.7240 },
        new Location { Name = "B", Latitude = 47.5050, Longitude = 8.7400 }
    ];

    [SetUp]
    public void Setup()
    {
        _handler = new StubHandler();
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _httpClientFactory.CreateClient(ExternalHttpClientConstants.RoutingClientName).Returns(new HttpClient(_handler));

        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(Arg.Any<string>()).Returns(Task.FromResult<SettingsEntity?>(null));

        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
        _builder = new DistanceMatrixBuilder(
            settingsReader,
            Substitute.For<ISettingsEncryptionService>(),
            _cache,
            Substitute.For<ILogger<DistanceMatrixBuilder>>(),
            _httpClientFactory,
            GeoServicesPolicyTestFactory.Default());
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
        _handler.Dispose();
    }

    [Test]
    public void Constructor_UsesNamedRoutingClient()
    {
        _httpClientFactory.Received(1).CreateClient(ExternalHttpClientConstants.RoutingClientName);
    }

    [Test]
    public async Task BuildDistanceMatrixAsync_OsrmAnswers_ReturnsRoadValuesNotEstimated()
    {
        // Arrange
        _handler.Respond(HttpStatusCode.OK, OsrmTableJson);

        // Act
        var result = await _builder.BuildDistanceMatrixAsync(TwoLocations, ContainerTransportMode.ByCar);

        // Assert
        result.IsEstimated.ShouldBeFalse();
        result.Matrix[0, 1].ShouldBe(2.47, 0.0001);
        result.DurationMatrix[0, 1].ShouldBe(144.2, 0.0001);
        result.Locations.ShouldBe(TwoLocations);
        _handler.LastRequestUri!.Scheme.ShouldBe(Uri.UriSchemeHttps);
        _handler.LastRequestUri.AbsoluteUri.ShouldStartWith(GeoServiceDefaults.OsrmBaseUrl);
    }

    [Test]
    public async Task BuildDistanceMatrixAsync_OsrmForbidden_FallsBackAndFlagsEstimated()
    {
        // Arrange
        _handler.Respond(HttpStatusCode.Forbidden, string.Empty);

        // Act
        var result = await _builder.BuildDistanceMatrixAsync(TwoLocations, ContainerTransportMode.ByCar);

        // Assert
        result.IsEstimated.ShouldBeTrue();
        result.Matrix[0, 1].ShouldBeGreaterThan(0);
        result.DurationMatrix[0, 1].ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task BuildDistanceMatrixAsync_FallbackIsNotCached_NextSuccessfulCallIsNotEstimated()
    {
        // Arrange
        _handler.Respond(HttpStatusCode.Forbidden, string.Empty);
        await _builder.BuildDistanceMatrixAsync(TwoLocations, ContainerTransportMode.ByCar);
        _handler.Respond(HttpStatusCode.OK, OsrmTableJson);

        // Act
        var result = await _builder.BuildDistanceMatrixAsync(TwoLocations, ContainerTransportMode.ByCar);

        // Assert
        result.IsEstimated.ShouldBeFalse();
    }

    [Test]
    public async Task BuildDistanceMatrixAsync_MixModeWithFailingRoutingService_FlagsEstimated()
    {
        // Arrange
        _handler.Respond(HttpStatusCode.Forbidden, string.Empty);

        // Act
        var result = await _builder.BuildDistanceMatrixAsync(TwoLocations, ContainerTransportMode.Mix);

        // Assert
        result.IsEstimated.ShouldBeTrue();
        result.DurationMatricesByProfile.ShouldNotBeNull();
    }

    [Test]
    public async Task BuildDistanceMatrixAsync_MixModeWithWorkingRoutingService_IsNotEstimated()
    {
        // Arrange
        _handler.Respond(HttpStatusCode.OK, OsrmTableJson);

        // Act
        var result = await _builder.BuildDistanceMatrixAsync(TwoLocations, ContainerTransportMode.Mix);

        // Assert
        result.IsEstimated.ShouldBeFalse();
    }

    [Test]
    public async Task BuildDistanceMatrixAsync_RoutingDisabled_UsesHaversineWithoutAnyRequest()
    {
        // Arrange
        var builder = CreateBuilder(GeoServicesPolicyTestFactory.NoOutbound());

        // Act
        var result = await builder.BuildDistanceMatrixAsync(TwoLocations, ContainerTransportMode.ByCar);

        // Assert
        result.IsEstimated.ShouldBeTrue();
        result.Matrix[0, 1].ShouldBeGreaterThan(0);
        _handler.LastRequestUri.ShouldBeNull();
    }

    [Test]
    public async Task BuildDistanceMatrixAsync_MixModeRoutingDisabled_EstimatesEveryProfileWithoutAnyRequest()
    {
        // Arrange
        var builder = CreateBuilder(GeoServicesPolicyTestFactory.NoOutbound());

        // Act
        var result = await builder.BuildDistanceMatrixAsync(TwoLocations, ContainerTransportMode.Mix);

        // Assert
        result.IsEstimated.ShouldBeTrue();
        result.DurationMatricesByProfile!.Count.ShouldBe(3);
        _handler.LastRequestUri.ShouldBeNull();
    }

    [Test]
    public async Task BuildDistanceMatrixAsync_OwnOsrmInstance_CallsTheConfiguredHost()
    {
        // Arrange
        _handler.Respond(HttpStatusCode.OK, OsrmTableJson);
        var builder = CreateBuilder(GeoServicesPolicyTestFactory.With(o =>
        {
            o.NoOutboundTraffic = true;
            o.Routing.BaseUrl = "http://osrm.internal:5000";
        }));

        // Act
        var result = await builder.BuildDistanceMatrixAsync(TwoLocations, ContainerTransportMode.ByCar);

        // Assert
        result.IsEstimated.ShouldBeFalse();
        _handler.LastRequestUri!.AbsoluteUri.ShouldStartWith("http://osrm.internal:5000/table/v1/driving/");
    }

    private DistanceMatrixBuilder CreateBuilder(Klacks.Api.Domain.Interfaces.GeoServices.IGeoServicesPolicy policy)
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(Arg.Any<string>()).Returns(Task.FromResult<SettingsEntity?>(null));
        return new DistanceMatrixBuilder(
            settingsReader,
            Substitute.For<ISettingsEncryptionService>(),
            _cache,
            Substitute.For<ILogger<DistanceMatrixBuilder>>(),
            _httpClientFactory,
            policy);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private HttpStatusCode _statusCode = HttpStatusCode.OK;
        private string _body = string.Empty;

        public Uri? LastRequestUri { get; private set; }

        public void Respond(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(_statusCode) { Content = new StringContent(_body) });
        }
    }
}
