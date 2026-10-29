// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for GetRouteQueryHandler: hands the waypoints and the transport mode to IRoutingService and maps the route
/// back to lat/lon resources; null (no route) stays null.
/// </summary>

using Klacks.Api.Application.DTOs.Routing;
using Klacks.Api.Application.Handlers.Routing;
using Klacks.Api.Application.Queries.Routing;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Routing;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Handlers.Routing;

[TestFixture]
public class GetRouteQueryHandlerTests
{
    private IRoutingService _routingService = null!;
    private GetRouteQueryHandler _handler = null!;

    private static List<RoutePointResource> Waypoints() =>
    [
        new RoutePointResource { Lat = 47.1, Lon = 8.2 },
        new RoutePointResource { Lat = 47.2, Lon = 8.3 },
    ];

    [SetUp]
    public void SetUp()
    {
        _routingService = Substitute.For<IRoutingService>();
        _handler = new GetRouteQueryHandler(_routingService);
    }

    [Test]
    public async Task Handle_WithoutTransportMode_RoutesByCar()
    {
        IReadOnlyList<RoutePoint>? passed = null;
        _routingService.GetRouteAsync(
                Arg.Do<IReadOnlyList<RoutePoint>>(w => passed = w), ContainerTransportMode.ByCar, Arg.Any<CancellationToken>())
            .Returns(new List<RoutePoint> { new(47.1, 8.2), new(47.15, 8.25) });

        var result = await _handler.Handle(new GetRouteQuery(Waypoints()), CancellationToken.None);

        passed.ShouldBe([new RoutePoint(47.1, 8.2), new RoutePoint(47.2, 8.3)]);
        result.ShouldNotBeNull();
        result.Select(p => (p.Lat, p.Lon)).ShouldBe([(47.1, 8.2), (47.15, 8.25)]);
    }

    [TestCase(ContainerTransportMode.ByBicycle)]
    [TestCase(ContainerTransportMode.ByFoot)]
    [TestCase(ContainerTransportMode.Mix)]
    public async Task Handle_PassesTheTransportModeOn(ContainerTransportMode transportMode)
    {
        _routingService.GetRouteAsync(Arg.Any<IReadOnlyList<RoutePoint>>(), transportMode, Arg.Any<CancellationToken>())
            .Returns(new List<RoutePoint> { new(47.1, 8.2) });

        var result = await _handler.Handle(new GetRouteQuery(Waypoints(), transportMode), CancellationToken.None);

        result.ShouldNotBeNull();
        await _routingService.Received(1).GetRouteAsync(
            Arg.Any<IReadOnlyList<RoutePoint>>(), transportMode, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_NoRoute_StaysNull()
    {
        _routingService.GetRouteAsync(
                Arg.Any<IReadOnlyList<RoutePoint>>(), Arg.Any<ContainerTransportMode>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RoutePoint>?)null);

        var result = await _handler.Handle(new GetRouteQuery(Waypoints()), CancellationToken.None);

        result.ShouldBeNull();
    }
}
