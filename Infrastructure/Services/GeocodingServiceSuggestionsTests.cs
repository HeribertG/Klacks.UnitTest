// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for GeocodingService.GetAddressSuggestionsAsync: Nominatim response parsing,
/// fallback query on empty results, caching, and HTTP error handling.
/// </summary>
/// <param name="mockHandler">MockHttpMessageHandler to control HTTP responses</param>
/// <param name="cache">In-memory cache for geocoding result caching</param>

using System.Net;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Services;

[TestFixture]
public class GeocodingServiceSuggestionsTests
{
    private MockHttpMessageHandler _mockHandler = null!;
    private IHttpClientFactory _httpClientFactory = null!;
    private MemoryCache _cache = null!;
    private ILogger<GeocodingService> _logger = null!;
    private GeocodingService _service = null!;

    [SetUp]
    public void Setup()
    {
        _mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(_mockHandler)
        {
            BaseAddress = new Uri("https://nominatim.openstreetmap.org")
        };

        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _httpClientFactory.CreateClient("Nominatim").Returns(httpClient);

        _cache = new MemoryCache(new MemoryCacheOptions());
        _logger = Substitute.For<ILogger<GeocodingService>>();

        _service = new GeocodingService(_httpClientFactory, _cache, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
        _mockHandler.Dispose();
    }

    [Test]
    public async Task GetAddressSuggestions_WithResults_ReturnsSuggestions()
    {
        // Arrange
        var jsonResponse = """
            [
                {"lat": "47.3769", "lon": "8.5417", "display_name": "Bahnhofstrasse, 8001 Zürich, Switzerland"},
                {"lat": "47.3780", "lon": "8.5400", "display_name": "Bahnhofstrasse, 8001 Zürich, Kreis 1"},
                {"lat": "47.3790", "lon": "8.5430", "display_name": "Bahnhofstrasse, 8002 Zürich, Switzerland"}
            ]
            """;

        _mockHandler.ResponseMessage = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse, System.Text.Encoding.UTF8, "application/json")
        };

        // Act
        var result = await _service.GetAddressSuggestionsAsync("Bahnhofstrasse", "8001", "Zürich", "CH");

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(3);
        result[0].DisplayName.Should().Be("Bahnhofstrasse, 8001 Zürich, Switzerland");
        result[0].Latitude.Should().Be(47.3769);
        result[0].Longitude.Should().Be(8.5417);
        result[1].Latitude.Should().Be(47.3780);
        result[2].Latitude.Should().Be(47.3790);
    }

    [Test]
    public async Task GetAddressSuggestions_NoResults_ReturnsFallback()
    {
        // Arrange
        var callCount = 0;
        var emptyResponse = "[]";
        var fallbackResponse = """
            [
                {"lat": "47.3769", "lon": "8.5417", "display_name": "8001 Zürich, Switzerland"}
            ]
            """;

        _mockHandler.SendAsyncFunc = _ =>
        {
            callCount++;
            if (callCount == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(emptyResponse, System.Text.Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(fallbackResponse, System.Text.Encoding.UTF8, "application/json")
            };
        };

        // Act
        var result = await _service.GetAddressSuggestionsAsync("Nonexistent", "8001", "Zürich", "CH");

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(1);
        result[0].DisplayName.Should().Be("8001 Zürich, Switzerland");
        callCount.Should().BeGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task GetAddressSuggestions_CachesResults()
    {
        // Arrange
        var callCount = 0;
        var jsonResponse = """
            [
                {"lat": "47.3769", "lon": "8.5417", "display_name": "Bahnhofstrasse, 8001 Zürich, Switzerland"}
            ]
            """;

        _mockHandler.SendAsyncFunc = _ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, System.Text.Encoding.UTF8, "application/json")
            };
        };

        // Act
        var result1 = await _service.GetAddressSuggestionsAsync("Bahnhofstrasse", "8001", "Zürich", "CH");
        var result2 = await _service.GetAddressSuggestionsAsync("Bahnhofstrasse", "8001", "Zürich", "CH");

        // Assert
        result1.Should().HaveCount(1);
        result2.Should().HaveCount(1);
        result1[0].DisplayName.Should().Be(result2[0].DisplayName);
        callCount.Should().Be(1);
    }

    [Test]
    public async Task GetAddressSuggestions_HttpError_ReturnsEmptyList()
    {
        // Arrange
        _mockHandler.ResponseMessage = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        // Act
        var result = await _service.GetAddressSuggestionsAsync("Bahnhofstrasse", "8001", "Zürich", "CH");

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Test]
    public async Task GetAddressSuggestions_InvalidJson_ReturnsEmptyList()
    {
        // Arrange
        _mockHandler.ResponseMessage = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("invalid json", System.Text.Encoding.UTF8, "application/json")
        };

        // Act
        var result = await _service.GetAddressSuggestionsAsync("Bahnhofstrasse", "8001", "Zürich", "CH");

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Test]
    public async Task GetAddressSuggestions_WithLimit_PassesLimitToQuery()
    {
        // Arrange
        string? capturedUrl = null;
        _mockHandler.SendAsyncFunc = request =>
        {
            capturedUrl = request.RequestUri?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json")
            };
        };

        // Act
        await _service.GetAddressSuggestionsAsync("Bahnhofstrasse", "8001", "Zürich", "CH", limit: 3);

        // Assert
        capturedUrl.Should().NotBeNull();
        capturedUrl.Should().Contain("limit=3");
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public HttpResponseMessage ResponseMessage { get; set; } = new(HttpStatusCode.OK);
        public Func<HttpRequestMessage, HttpResponseMessage>? SendAsyncFunc { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (SendAsyncFunc != null)
            {
                return Task.FromResult(SendAsyncFunc(request));
            }

            return Task.FromResult(ResponseMessage);
        }
    }
}
