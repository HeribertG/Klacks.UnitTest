// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the retry of the OpenRouteService matrix request in DistanceMatrixBuilder: transient answers (5xx, 429,
/// network errors) are repeated up to OpenRouteServiceRetryConstants.MaxAttempts, other 4xx answers are not, and a
/// cancelled caller token ends the call with an exception instead of a Haversine estimate.
/// The retry pauses are real (1 s, then 2 s), so this fixture takes about 6 s. The timeout case is simulated by a
/// TaskCanceledException from the handler; the 30 s per-attempt limit itself is not waited for.
/// </summary>

using System.Net;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.RouteOptimization;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Services.RouteOptimization;

[TestFixture]
public class DistanceMatrixBuilderOpenRouteServiceRetryTests
{
    private const string ApiKey = "ors-test-key";
    private const string StoredValue = "encrypted-ors-key";
    private const string OrsMatrixJson = """
        {"distances":[[0,2470],[2510,0]],"durations":[[0,144.2],[150.8,0]]}
        """;
    private const string ProxyErrorJson = """{"error":"There was a problem proxying the request"}""";

    private static readonly List<Location> TwoLocations =
    [
        new Location { Name = "A", Latitude = 47.4990, Longitude = 8.7240 },
        new Location { Name = "B", Latitude = 47.5050, Longitude = 8.7400 }
    ];

    private QueueHandler _handler = null!;
    private MemoryCache _cache = null!;
    private DistanceMatrixBuilder _builder = null!;

    [SetUp]
    public void Setup()
    {
        _handler = new QueueHandler();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(ExternalHttpClientConstants.RoutingClientName).Returns(new HttpClient(_handler));

        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(SettingKeys.OpenRouteServiceApiKey).Returns(Task.FromResult<SettingsEntity?>(
            new SettingsEntity { Type = SettingKeys.OpenRouteServiceApiKey, Value = StoredValue }));
        var encryptionService = Substitute.For<ISettingsEncryptionService>();
        encryptionService.ProcessForReading(SettingKeys.OpenRouteServiceApiKey, StoredValue).Returns(ApiKey);

        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
        _builder = new DistanceMatrixBuilder(
            settingsReader,
            encryptionService,
            _cache,
            Substitute.For<ILogger<DistanceMatrixBuilder>>(),
            httpClientFactory,
            GeoServicesPolicyTestFactory.Default());
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
        _handler.Dispose();
    }

    [Test]
    public async Task BuildDistanceMatrixAsync_OrsAnswers500ThenOk_RetriesAndReturnsRoadValues()
    {
        // Arrange
        _handler.Enqueue(HttpStatusCode.InternalServerError, ProxyErrorJson);
        _handler.Enqueue(HttpStatusCode.OK, OrsMatrixJson);

        // Act
        var result = await _builder.BuildDistanceMatrixAsync(TwoLocations, ContainerTransportMode.ByCar);

        // Assert
        _handler.CallCount.ShouldBe(2);
        result.IsEstimated.ShouldBeFalse();
        result.Matrix[0, 1].ShouldBe(2.47, 0.0001);
        result.DurationMatrix[0, 1].ShouldBe(144.2, 0.0001);
        _handler.AuthorizationHeaders.ShouldAllBe(header => header == ApiKey);
    }

    [Test]
    public async Task BuildDistanceMatrixAsync_OrsNetworkErrorThenOk_RetriesAndReturnsRoadValues()
    {
        // Arrange
        _handler.EnqueueFailure(new HttpRequestException("connection reset"));
        _handler.Enqueue(HttpStatusCode.OK, OrsMatrixJson);

        // Act
        var result = await _builder.BuildDistanceMatrixAsync(TwoLocations, ContainerTransportMode.ByCar);

        // Assert
        _handler.CallCount.ShouldBe(2);
        result.IsEstimated.ShouldBeFalse();
    }

    [Test]
    public async Task BuildDistanceMatrixAsync_OrsAttemptTimesOutThenOk_RetriesAndReturnsRoadValues()
    {
        // Arrange
        _handler.EnqueueFailure(new TaskCanceledException("attempt timed out", new TimeoutException()));
        _handler.Enqueue(HttpStatusCode.OK, OrsMatrixJson);

        // Act
        var result = await _builder.BuildDistanceMatrixAsync(TwoLocations, ContainerTransportMode.ByCar);

        // Assert
        _handler.CallCount.ShouldBe(2);
        result.IsEstimated.ShouldBeFalse();
        result.Matrix[0, 1].ShouldBe(2.47, 0.0001);
    }

    [Test]
    public async Task BuildDistanceMatrixAsync_OrsAnswers500EveryTime_StopsAfterMaxAttemptsAndFallsBack()
    {
        // Arrange
        _handler.Enqueue(HttpStatusCode.InternalServerError, ProxyErrorJson);
        _handler.Enqueue(HttpStatusCode.BadGateway, ProxyErrorJson);
        _handler.Enqueue(HttpStatusCode.ServiceUnavailable, ProxyErrorJson);
        _handler.Enqueue(HttpStatusCode.OK, OrsMatrixJson);

        // Act
        var result = await _builder.BuildDistanceMatrixAsync(TwoLocations, ContainerTransportMode.ByCar);

        // Assert
        _handler.CallCount.ShouldBe(OpenRouteServiceRetryConstants.MaxAttempts);
        result.IsEstimated.ShouldBeTrue();
    }

    [Test]
    public async Task BuildDistanceMatrixAsync_OrsAnswers401_DoesNotRetry()
    {
        // Arrange
        _handler.Enqueue(HttpStatusCode.Unauthorized, string.Empty);
        _handler.Enqueue(HttpStatusCode.OK, OrsMatrixJson);

        // Act
        var result = await _builder.BuildDistanceMatrixAsync(TwoLocations, ContainerTransportMode.ByCar);

        // Assert
        _handler.CallCount.ShouldBe(1);
        result.IsEstimated.ShouldBeTrue();
    }

    [Test]
    public async Task BuildDistanceMatrixAsync_CallerCancelsDuringRetry_ThrowsInsteadOfEstimating()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        _handler.Enqueue(HttpStatusCode.InternalServerError, ProxyErrorJson, onSend: cancellation.Cancel);
        _handler.Enqueue(HttpStatusCode.OK, OrsMatrixJson);

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(
            () => _builder.BuildDistanceMatrixAsync(TwoLocations, ContainerTransportMode.ByCar, cancellation.Token));
        _handler.CallCount.ShouldBe(1);
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _answers = new();

        public int CallCount { get; private set; }

        public List<string?> AuthorizationHeaders { get; } = [];

        public void Enqueue(HttpStatusCode statusCode, string body, Action? onSend = null)
        {
            _answers.Enqueue(() =>
            {
                onSend?.Invoke();
                return new HttpResponseMessage(statusCode) { Content = new StringContent(body) };
            });
        }

        public void EnqueueFailure(Exception exception)
        {
            _answers.Enqueue(() => throw exception);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            AuthorizationHeaders.Add(request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : null);
            return Task.FromResult(_answers.Dequeue()());
        }
    }
}
