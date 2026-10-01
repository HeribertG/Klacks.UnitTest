// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for RouteDirectionsBuilder: directions are requested over HTTPS through the named routing
/// client, and a failing segment yields an empty placeholder instead of breaking the whole route.
/// </summary>

using System.Net;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.RouteOptimization;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.RouteOptimization;

[TestFixture]
public class RouteDirectionsBuilderTests
{
    private const string OsrmRouteJson = """
        {"code":"Ok","routes":[{"legs":[{"distance":2470.0,"duration":144.0,"steps":[
          {"maneuver":{"type":"depart"},"name":"Stadthausstrasse","distance":300.0,"duration":30.0},
          {"maneuver":{"type":"turn","modifier":"left"},"name":"Technikumstrasse","distance":2170.0,"duration":114.0},
          {"maneuver":{"type":"arrive"},"name":"","distance":0.0,"duration":0.0}]}]}]}
        """;

    private StubHandler _handler = null!;
    private IHttpClientFactory _httpClientFactory = null!;
    private RouteDirectionsBuilder _builder = null!;

    private static readonly List<Location> Route =
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
        _builder = new RouteDirectionsBuilder(Substitute.For<ILogger<RouteDirectionsBuilder>>(), _httpClientFactory);
    }

    [TearDown]
    public void TearDown()
    {
        _handler.Dispose();
    }

    [Test]
    public async Task GetRouteDirectionsAsync_OsrmAnswers_ReturnsStepsViaNamedHttpsClient()
    {
        // Arrange
        _handler.Respond(HttpStatusCode.OK, OsrmRouteJson);
        var matrix = new DistanceMatrix(Route, new double[2, 2], new double[2, 2]);

        // Act
        var result = await _builder.GetRouteDirectionsAsync(Route, matrix, ContainerTransportMode.ByCar);

        // Assert
        _httpClientFactory.Received(1).CreateClient(ExternalHttpClientConstants.RoutingClientName);
        _handler.LastRequestUri!.AbsoluteUri.ShouldStartWith(RoutingServiceUrls.OsrmBaseUrl);
        result.Count.ShouldBe(1);
        result[0].DistanceKm.ShouldBe(2.47, 0.0001);
        result[0].Steps.Count.ShouldBe(2);
    }

    [Test]
    public async Task GetRouteDirectionsAsync_OsrmForbidden_ReturnsEmptySegment()
    {
        // Arrange
        _handler.Respond(HttpStatusCode.Forbidden, string.Empty);
        var matrix = new DistanceMatrix(Route, new double[2, 2], new double[2, 2]);

        // Act
        var result = await _builder.GetRouteDirectionsAsync(Route, matrix, ContainerTransportMode.ByCar);

        // Assert
        result.Count.ShouldBe(1);
        result[0].Steps.ShouldBeEmpty();
        result[0].DistanceKm.ShouldBe(0);
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
