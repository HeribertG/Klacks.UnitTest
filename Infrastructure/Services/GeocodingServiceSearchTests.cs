// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for GeocodingService.SearchAsync (the free-text search behind the dashboard search box): Found,
/// NotFound and Unavailable, the accept-language parameter, the configured Nominatim base URL, caching, the bounded
/// wait for the Nominatim slot shared with address validation, and timeouts (Unavailable) versus caller cancellation
/// (rethrown).
/// </summary>

using System.Net;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Services;

[TestFixture]
public class GeocodingServiceSearchTests
{
    private const string FoundJson = """
        [{"lat": "47.3769", "lon": "8.5417", "display_name": "Zürich, Schweiz"}]
        """;

    private StubHandler _handler = null!;
    private IHttpClientFactory _factory = null!;
    private MemoryCache _cache = null!;
    private GeocodingService _service = null!;

    [SetUp]
    public void Setup()
    {
        _handler = new StubHandler();
        _factory = Substitute.For<IHttpClientFactory>();
        _factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(_handler));
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
        _service = CreateService(GeoServicesPolicyTestFactory.Default());
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
        _handler.Dispose();
    }

    [Test]
    public async Task SearchAsync_Hit_ReturnsFoundWithCoordinatesAndLanguage()
    {
        _handler.Respond = _ => Json(FoundJson);

        var result = await _service.SearchAsync("Zürich", "de-CH");

        result.Status.ShouldBe(GeocodingSearchOutcome.Found);
        result.Latitude.ShouldBe(47.3769);
        result.Longitude.ShouldBe(8.5417);
        result.DisplayName.ShouldBe("Zürich, Schweiz");
        _handler.LastRequestUri!.AbsoluteUri.ShouldStartWith(GeoServiceDefaults.NominatimBaseUrl + GeoServicePaths.NominatimSearch);
        _handler.LastRequestUri.Query.ShouldContain("accept-language=de-CH");
        _handler.LastRequestUri.Query.ShouldContain("limit=1");
    }

    [Test]
    public async Task SearchAsync_EmptyAnswer_ReturnsNotFound()
    {
        _handler.Respond = _ => Json("[]");

        var result = await _service.SearchAsync("Nirgendwo", null);

        result.Status.ShouldBe(GeocodingSearchOutcome.NotFound);
        _handler.LastRequestUri!.Query.ShouldNotContain("accept-language");
    }

    [Test]
    public async Task SearchAsync_NonSuccess_ReturnsUnavailableAndIsNotCached()
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        var result = await _service.SearchAsync("Bern", "de");

        result.Status.ShouldBe(GeocodingSearchOutcome.Unavailable);
        _cache.Count.ShouldBe(0);
    }

    [Test]
    public async Task SearchAsync_NetworkError_ReturnsUnavailable()
    {
        _handler.Respond = _ => throw new HttpRequestException("connection refused");

        var result = await _service.SearchAsync("Bern", "de");

        result.Status.ShouldBe(GeocodingSearchOutcome.Unavailable);
    }

    [Test]
    public async Task SearchAsync_SameQueryTwice_SecondIsServedFromCache()
    {
        _handler.Respond = _ => Json(FoundJson);

        await _service.SearchAsync("Zürich", "de");
        var second = await _service.SearchAsync("Zürich", "de");

        second.Status.ShouldBe(GeocodingSearchOutcome.Found);
        _handler.Requests.ShouldBe(1);
    }

    [Test]
    public async Task SearchAsync_OwnNominatimInstance_CallsTheConfiguredHost()
    {
        _handler.Respond = _ => Json(FoundJson);
        var service = CreateService(GeoServicesPolicyTestFactory.With(o =>
        {
            o.NoOutboundTraffic = true;
            o.Geocoding.BaseUrl = "http://nominatim.internal:8080/";
        }));

        var result = await service.SearchAsync("Zürich", null);

        result.Status.ShouldBe(GeocodingSearchOutcome.Found);
        _handler.LastRequestUri!.AbsoluteUri.ShouldStartWith("http://nominatim.internal:8080/search?");
    }

    [Test]
    public async Task SearchAsync_HttpClientTimeout_ReturnsUnavailable()
    {
        _handler.RespondAsync = async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>())
            .Returns(new HttpClient(_handler, disposeHandler: false) { Timeout = TimeSpan.FromMilliseconds(200) });
        var service = new GeocodingService(
            factory, _cache, GeoServicesPolicyTestFactory.Default(), Substitute.For<ILogger<GeocodingService>>());

        var result = await service.SearchAsync("Bern", "de");

        result.Status.ShouldBe(GeocodingSearchOutcome.Unavailable);
        _cache.Count.ShouldBe(0);
    }

    [Test]
    public async Task SearchAsync_TimeoutSurfacingAsTaskCanceled_ReturnsUnavailable()
    {
        _handler.Respond = _ => throw new TaskCanceledException("timed out", new TimeoutException());

        var result = await _service.SearchAsync("Bern", "de");

        result.Status.ShouldBe(GeocodingSearchOutcome.Unavailable);
    }

    [Test]
    public async Task SearchAsync_CallerCancels_IsRethrown()
    {
        using var cancellation = new CancellationTokenSource();
        _handler.RespondAsync = async (_, cancellationToken) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        await Should.ThrowAsync<OperationCanceledException>(() => _service.SearchAsync("Bern", "de", cancellation.Token));
    }

    [Test]
    public async Task SearchAsync_SlotHeldByAddressValidation_GivesUpAfterTheBoundedWait_WithoutDisturbingValidation()
    {
        var validationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseValidation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.RespondAsync = async (request, _) =>
        {
            if (request.RequestUri!.Query.Contains("postalcode=", StringComparison.Ordinal))
            {
                validationEntered.TrySetResult();
                await releaseValidation.Task;
            }

            return Json(FoundJson);
        };

        var validation = _service.ValidateExactAddressAsync("Bahnhofstrasse 1", "8001", "Zürich", "Switzerland");
        await validationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var started = System.Diagnostics.Stopwatch.StartNew();
        var blocked = await _service.SearchAsync("Bern", "de");
        started.Stop();

        blocked.Status.ShouldBe(GeocodingSearchOutcome.Unavailable);
        started.Elapsed.ShouldBeGreaterThanOrEqualTo(GeocodingService.SearchSlotWaitTimeout - TimeSpan.FromMilliseconds(100));
        _cache.TryGetValue("geo_search_Bern_de", out _).ShouldBeFalse();

        releaseValidation.SetResult();
        var validated = await validation.WaitAsync(TimeSpan.FromSeconds(10));
        validated.Found.ShouldBeTrue();
        validated.ServiceUnavailable.ShouldBeFalse();

        var afterwards = await _service.SearchAsync("Bern", "de");
        afterwards.Status.ShouldBe(GeocodingSearchOutcome.Found);
    }

    private GeocodingService CreateService(Klacks.Api.Domain.Interfaces.GeoServices.IGeoServicesPolicy policy) =>
        new(_factory, _cache, policy, Substitute.For<ILogger<GeocodingService>>());

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? RespondAsync { get; set; }

        public Uri? LastRequestUri { get; private set; }

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastRequestUri = request.RequestUri;
            return RespondAsync != null ? RespondAsync(request, cancellationToken) : Task.FromResult(Respond(request));
        }
    }
}
