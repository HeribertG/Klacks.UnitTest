// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for GeocodingService with geocoding switched off by the geo service policy: no method sends an HTTP
/// request or writes the cache, and the validation result says Disabled (not ServiceUnavailable, not "not found").
/// </summary>

using System.Net;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Services;

[TestFixture]
public class GeocodingServiceDisabledTests
{
    private CountingHandler _handler = null!;
    private MemoryCache _cache = null!;
    private GeocodingService _service = null!;

    [SetUp]
    public void Setup()
    {
        _handler = new CountingHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(_handler));
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
        _service = new GeocodingService(
            factory, _cache, GeoServicesPolicyTestFactory.NoOutbound(), Substitute.For<ILogger<GeocodingService>>());
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
        _handler.Dispose();
    }

    [Test]
    public void IsEnabled_NoOutboundWithoutOwnUrl_IsFalse()
    {
        _service.IsEnabled.ShouldBeFalse();
    }

    [Test]
    public async Task GeocodeAsync_Disabled_ReturnsNullWithoutRequestOrCache()
    {
        var (latitude, longitude) = await _service.GeocodeAsync("Bern", "Schweiz");

        latitude.ShouldBeNull();
        longitude.ShouldBeNull();
        AssertNoTraffic();
    }

    [Test]
    public async Task GeocodeAddressAsync_Disabled_ReturnsNullWithoutRequestOrCache()
    {
        var (latitude, longitude) = await _service.GeocodeAddressAsync("Bundesplatz 3, 3011 Bern", "Schweiz");

        latitude.ShouldBeNull();
        longitude.ShouldBeNull();
        AssertNoTraffic();
    }

    [Test]
    public async Task ValidateExactAddressAsync_Disabled_ReportsDisabledNotUnavailable()
    {
        var result = await _service.ValidateExactAddressAsync("Bundesplatz 3", "3011", "Bern", "Schweiz");

        result.Disabled.ShouldBeTrue();
        result.ServiceUnavailable.ShouldBeFalse();
        result.Found.ShouldBeFalse();
        AssertNoTraffic();
    }

    [Test]
    public async Task GetAddressSuggestionsAsync_Disabled_ReturnsEmptyWithoutRequestOrCache()
    {
        var result = await _service.GetAddressSuggestionsAsync("Bundesplatz 3", "3011", "Bern", "Schweiz");

        result.ShouldBeEmpty();
        AssertNoTraffic();
    }

    [Test]
    public async Task SearchAsync_Disabled_ReturnsDisabledWithoutRequestOrCache()
    {
        var result = await _service.SearchAsync("Bern", "de");

        result.Status.ShouldBe(GeocodingSearchOutcome.Disabled);
        result.Latitude.ShouldBeNull();
        AssertNoTraffic();
    }

    private void AssertNoTraffic()
    {
        _handler.Requests.ShouldBe(0);
        _cache.Count.ShouldBe(0);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        }
    }
}
