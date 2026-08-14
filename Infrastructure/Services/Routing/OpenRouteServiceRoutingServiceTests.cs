// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for OpenRouteServiceRoutingService: the API key stays on the server, is sent decrypted to
/// OpenRouteService, and every failure path returns null so the caller can fall back to OSRM.
/// </summary>

using System.Net;
using System.Text;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Infrastructure.Services.Routing;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Routing;

[TestFixture]
public class OpenRouteServiceRoutingServiceTests
{
    private const string SettingsType = "OPENROUTESERVICE_API_KEY";
    private const string StoredValue = "ENC:cipher-text";
    private const string DecryptedKey = "the-real-ors-key";

    private static readonly IReadOnlyList<RoutePoint> Waypoints = new List<RoutePoint>
    {
        new(47.3769, 8.5417),
        new(46.9480, 7.4474),
    };

    private MockHttpMessageHandler _handler = null!;
    private ISettingsRepository _settingsRepository = null!;
    private ISettingsEncryptionService _encryptionService = null!;
    private OpenRouteServiceRoutingService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _handler = new MockHttpMessageHandler();
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _encryptionService = Substitute.For<ISettingsEncryptionService>();

        _service = new OpenRouteServiceRoutingService(
            new HttpClient(_handler),
            _settingsRepository,
            _encryptionService,
            Substitute.For<ILogger<OpenRouteServiceRoutingService>>());
    }

    [TearDown]
    public void TearDown() => _handler.Dispose();

    [Test]
    public async Task GetRouteAsync_WithoutConfiguredKey_ShouldReturnNullWithoutCallingOrs()
    {
        _settingsRepository.GetSetting(SettingsType).Returns((Klacks.Api.Domain.Models.Settings.Settings?)null);

        var result = await _service.GetRouteAsync(Waypoints, CancellationToken.None);

        result.ShouldBeNull();
        _handler.WasCalled.ShouldBeFalse("no key means no outbound call at all");
    }

    [Test]
    public async Task GetRouteAsync_WithConfiguredKey_ShouldSendTheDecryptedKeyNotThePlaceholder()
    {
        ArrangeKey();
        ArrangeOrsResponse(HttpStatusCode.OK, """
            {"features":[{"geometry":{"coordinates":[[8.5417,47.3769],[7.4474,46.9480]]}}]}
            """);

        var result = await _service.GetRouteAsync(Waypoints, CancellationToken.None);

        result.ShouldNotBeNull();
        _handler.WasCalled.ShouldBeTrue();

        _handler.SentAuthorization.ShouldBe(DecryptedKey);
        _handler.SentAuthorization.ShouldNotBe(Klacks.Api.Application.Constants.SecretMask.Placeholder,
            "sending the mask is what silently broke routing before the proxy existed");
    }

    [Test]
    public async Task GetRouteAsync_WithConfiguredKey_ShouldMapGeoJsonToLatLonOrder()
    {
        ArrangeKey();
        ArrangeOrsResponse(HttpStatusCode.OK, """
            {"features":[{"geometry":{"coordinates":[[8.5417,47.3769],[7.4474,46.9480]]}}]}
            """);

        var result = await _service.GetRouteAsync(Waypoints, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Count.ShouldBe(2);
        result[0].Lat.ShouldBe(47.3769);
        result[0].Lon.ShouldBe(8.5417);
        result[1].Lat.ShouldBe(46.9480);
        result[1].Lon.ShouldBe(7.4474);
    }

    [Test]
    public async Task GetRouteAsync_WhenOrsRejectsTheRequest_ShouldReturnNull()
    {
        ArrangeKey();
        ArrangeOrsResponse(HttpStatusCode.Forbidden, "{}");

        var result = await _service.GetRouteAsync(Waypoints, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Test]
    public async Task GetRouteAsync_WhenOrsReturnsNoGeometry_ShouldReturnNull()
    {
        ArrangeKey();
        ArrangeOrsResponse(HttpStatusCode.OK, """{"features":[]}""");

        var result = await _service.GetRouteAsync(Waypoints, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Test]
    public async Task GetRouteAsync_WhenTheCallThrows_ShouldReturnNull()
    {
        ArrangeKey();
        _handler.Thrower = () => throw new HttpRequestException("network down");

        var result = await _service.GetRouteAsync(Waypoints, CancellationToken.None);

        result.ShouldBeNull();
    }

    private void ArrangeKey()
    {
        _settingsRepository.GetSetting(SettingsType).Returns(new Klacks.Api.Domain.Models.Settings.Settings
        {
            Type = SettingsType,
            Value = StoredValue,
        });
        _encryptionService.ProcessForReading(SettingsType, StoredValue).Returns(DecryptedKey);
    }

    private void ArrangeOrsResponse(HttpStatusCode statusCode, string body)
    {
        _handler.ResponseFactory = () => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public bool WasCalled { get; private set; }

        public string? SentAuthorization { get; private set; }

        public Func<HttpResponseMessage>? ResponseFactory { get; set; }

        public Action? Thrower { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            SentAuthorization = request.Headers.TryGetValues("Authorization", out var values)
                ? values.FirstOrDefault()
                : null;

            Thrower?.Invoke();

            return Task.FromResult(ResponseFactory?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
