// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests that TravelTimeCalculationService queries OSRM over HTTPS through the named routing client,
/// which carries the User-Agent the public OSRM server requires.
/// </summary>

using System.Net;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Infrastructure.Services;

[TestFixture]
public class TravelTimeCalculationServiceRoutingClientTests
{
    private const string OsrmRouteJson = """{"code":"Ok","routes":[{"duration":420.0}]}""";

    private StubHandler _handler = null!;
    private IHttpClientFactory _httpClientFactory = null!;
    private MemoryCache _cache = null!;
    private TravelTimeCalculationService _service = null!;

    [SetUp]
    public void Setup()
    {
        _handler = new StubHandler();
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _httpClientFactory.CreateClient(ExternalHttpClientConstants.RoutingClientName).Returns(new HttpClient(_handler));

        var settingsRepository = Substitute.For<ISettingsRepository>();
        settingsRepository.GetSetting(Arg.Any<string>()).Returns(Task.FromResult<SettingsEntity?>(null));

        _cache = new MemoryCache(new MemoryCacheOptions());
        _service = new TravelTimeCalculationService(
            settingsRepository,
            Substitute.For<ISettingsEncryptionService>(),
            Substitute.For<IGeocodingService>(),
            _cache,
            _httpClientFactory,
            Substitute.For<ILogger<TravelTimeCalculationService>>());
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
        _handler.Dispose();
    }

    [Test]
    public async Task CalculateTravelTimeAsync_NoOpenRouteServiceKey_UsesOsrmOverHttpsWithNamedClient()
    {
        // Arrange
        var from = new Address { Latitude = 47.4990, Longitude = 8.7240 };
        var to = new Address { Latitude = 47.5050, Longitude = 8.7400 };

        // Act
        var result = await _service.CalculateTravelTimeAsync(from, to, CancellationToken.None);

        // Assert
        _httpClientFactory.Received(1).CreateClient(ExternalHttpClientConstants.RoutingClientName);
        _handler.LastRequestUri!.Scheme.ShouldBe(Uri.UriSchemeHttps);
        _handler.LastRequestUri.AbsoluteUri.ShouldStartWith(RoutingServiceUrls.OsrmRouteDrivingUrl);
        result.ShouldBe(TimeSpan.FromSeconds(420));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OsrmRouteJson) });
        }
    }
}
