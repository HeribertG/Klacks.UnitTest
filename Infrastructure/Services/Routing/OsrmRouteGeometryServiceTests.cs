// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for OsrmRouteGeometryService: requests the full GeoJSON geometry from the configured OSRM instance through
/// the named routing client with culture-invariant lon,lat pairs, maps OSRM's answer to Ok / NoRoute / Unavailable,
/// and sends no request at all when routing is disabled.
/// </summary>

using System.Globalization;
using System.Net;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Services.Routing;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Routing;

[TestFixture]
public class OsrmRouteGeometryServiceTests
{
    private const string OwnOsrmBaseUrl = "https://osrm.internal.test";

    private const string OkJson = """
        {"code":"Ok","routes":[{"geometry":{"type":"LineString","coordinates":[[8.2,47.1],[8.25,47.15],[8.3,47.2]]}}]}
        """;

    private const string NoRouteJson = """{"code":"NoRoute","message":"Impossible route between points"}""";

    private static readonly IReadOnlyList<RoutePoint> Waypoints = [new RoutePoint(47.1, 8.2), new RoutePoint(47.2, 8.3)];

    private StubHandler _handler = null!;
    private IHttpClientFactory _httpClientFactory = null!;

    [SetUp]
    public void SetUp()
    {
        _handler = new StubHandler();
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _httpClientFactory.CreateClient(ExternalHttpClientConstants.RoutingClientName).Returns(_ => new HttpClient(_handler, disposeHandler: false));
    }

    [TearDown]
    public void TearDown()
    {
        _handler.Dispose();
    }

    private OsrmRouteGeometryService CreateService(Klacks.Api.Domain.Interfaces.GeoServices.IGeoServicesPolicy? policy = null) =>
        new(_httpClientFactory, policy ?? GeoServicesPolicyTestFactory.Default(), Substitute.For<ILogger<OsrmRouteGeometryService>>());

    [Test]
    public async Task GetRouteGeometryAsync_OsrmAnswersOk_ReturnsLatLonGeometryViaNamedClient()
    {
        _handler.Respond(HttpStatusCode.OK, OkJson);

        var result = await CreateService().GetRouteGeometryAsync(Waypoints, ContainerTransportMode.ByCar, CancellationToken.None);

        _httpClientFactory.Received(1).CreateClient(ExternalHttpClientConstants.RoutingClientName);
        result.Status.ShouldBe(RouteGeometryStatus.Ok);
        result.Points.ShouldBe([new RoutePoint(47.1, 8.2), new RoutePoint(47.15, 8.25), new RoutePoint(47.2, 8.3)]);
        _handler.LastRequestUri!.AbsoluteUri.ShouldBe(
            $"{GeoServiceDefaults.OsrmBaseUrl}{GeoServicePaths.OsrmRouteDriving}/8.200000,47.100000;8.300000,47.200000?overview=full&geometries=geojson");
    }

    [TestCase(ContainerTransportMode.ByCar, "driving")]
    [TestCase(ContainerTransportMode.ByBicycle, "cycling")]
    [TestCase(ContainerTransportMode.ByFoot, "foot")]
    [TestCase(ContainerTransportMode.Mix, "driving")]
    public async Task GetRouteGeometryAsync_TransportMode_RequestsTheOsrmProfileOfTheMode(
        ContainerTransportMode transportMode, string expectedProfile)
    {
        _handler.Respond(HttpStatusCode.OK, OkJson);

        await CreateService().GetRouteGeometryAsync(Waypoints, transportMode, CancellationToken.None);

        _handler.LastRequestUri!.AbsoluteUri.ShouldStartWith(
            $"{GeoServiceDefaults.OsrmBaseUrl}{GeoServicePaths.OsrmRoute}/{expectedProfile}/8.200000,47.100000;");
    }

    [Test]
    public async Task GetRouteGeometryAsync_OwnInstance_UsesConfiguredBaseUrl()
    {
        _handler.Respond(HttpStatusCode.OK, OkJson);
        var policy = GeoServicesPolicyTestFactory.With(options => options.Routing.BaseUrl = OwnOsrmBaseUrl);

        await CreateService(policy).GetRouteGeometryAsync(Waypoints, ContainerTransportMode.ByCar, CancellationToken.None);

        _handler.LastRequestUri!.AbsoluteUri.ShouldStartWith($"{OwnOsrmBaseUrl}{GeoServicePaths.OsrmRouteDriving}/");
    }

    [Test]
    public async Task GetRouteGeometryAsync_CommaDecimalCulture_StillSendsInvariantCoordinates()
    {
        _handler.Respond(HttpStatusCode.OK, OkJson);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            await CreateService().GetRouteGeometryAsync(Waypoints, ContainerTransportMode.ByCar, CancellationToken.None);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        _handler.LastRequestUri!.AbsoluteUri.ShouldContain("/8.200000,47.100000;8.300000,47.200000?");
    }

    [Test]
    public async Task GetRouteGeometryAsync_RoutingDisabled_ReturnsDisabledWithoutAnyRequest()
    {
        var result = await CreateService(GeoServicesPolicyTestFactory.NoOutbound()).GetRouteGeometryAsync(Waypoints, ContainerTransportMode.ByCar, CancellationToken.None);

        result.Status.ShouldBe(RouteGeometryStatus.Disabled);
        result.Points.ShouldBeEmpty();
        _handler.RequestCount.ShouldBe(0);
    }

    [TestCase(HttpStatusCode.OK)]
    [TestCase(HttpStatusCode.BadRequest)]
    public async Task GetRouteGeometryAsync_OsrmReportsNoRoute_ReturnsNoRoute(HttpStatusCode statusCode)
    {
        _handler.Respond(statusCode, NoRouteJson);

        var result = await CreateService().GetRouteGeometryAsync(Waypoints, ContainerTransportMode.ByCar, CancellationToken.None);

        result.Status.ShouldBe(RouteGeometryStatus.NoRoute);
        result.Points.ShouldBeEmpty();
    }

    [TestCase(HttpStatusCode.ServiceUnavailable, "")]
    [TestCase(HttpStatusCode.InternalServerError, "<html>error</html>")]
    [TestCase(HttpStatusCode.BadRequest, """{"code":"InvalidQuery"}""")]
    [TestCase(HttpStatusCode.OK, """{"code":"Ok","routes":[]}""")]
    [TestCase(HttpStatusCode.OK, "not json")]
    public async Task GetRouteGeometryAsync_FailureOrUnusableAnswer_ReturnsUnavailable(HttpStatusCode statusCode, string body)
    {
        _handler.Respond(statusCode, body);

        var result = await CreateService().GetRouteGeometryAsync(Waypoints, ContainerTransportMode.ByCar, CancellationToken.None);

        result.Status.ShouldBe(RouteGeometryStatus.Unavailable);
        result.Points.ShouldBeEmpty();
    }

    [Test]
    public async Task GetRouteGeometryAsync_NetworkError_ReturnsUnavailable()
    {
        _handler.Throw(new HttpRequestException("connection refused"));

        var result = await CreateService().GetRouteGeometryAsync(Waypoints, ContainerTransportMode.ByCar, CancellationToken.None);

        result.Status.ShouldBe(RouteGeometryStatus.Unavailable);
    }

    [Test]
    public async Task GetRouteGeometryAsync_CallerCancels_Throws()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => CreateService().GetRouteGeometryAsync(Waypoints, ContainerTransportMode.ByCar, cancellation.Token));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private HttpStatusCode _statusCode = HttpStatusCode.OK;
        private string _body = string.Empty;
        private Exception? _exception;

        public Uri? LastRequestUri { get; private set; }

        public int RequestCount { get; private set; }

        public void Respond(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        public void Throw(Exception exception)
        {
            _exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            LastRequestUri = request.RequestUri;
            if (_exception != null)
            {
                throw _exception;
            }

            return Task.FromResult(new HttpResponseMessage(_statusCode) { Content = new StringContent(_body) });
        }
    }
}
