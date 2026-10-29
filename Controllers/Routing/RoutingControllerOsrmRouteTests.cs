// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for RoutingController.OsrmRoute and Directions: forwards the waypoints and the optional transport mode (car
/// when omitted) to their queries, OsrmRoute answers 200 OK with the status-bearing geometry (also for Disabled, never
/// 204) and is rate limited with the geo-proxy policy, Directions answers 204 when no route comes back.
/// </summary>
namespace Klacks.UnitTest.Controllers.Routing;

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Routing;
using Klacks.Api.Application.Queries.Routing;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.UserBackend.Routing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class RoutingControllerOsrmRouteTests
{
    private IMediator _mediator = null!;
    private RoutingController _controller = null!;

    private static List<RoutePointResource> Waypoints() =>
    [
        new RoutePointResource { Lat = 47.1, Lon = 8.2 },
        new RoutePointResource { Lat = 47.2, Lon = 8.3 },
    ];

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _controller = new RoutingController(_mediator);
    }

    [Test]
    public async Task OsrmRoute_ReturnsMediatorResultAsOk()
    {
        var waypoints = Waypoints();
        var expected = new RouteGeometryResource { Status = RouteGeometryStatus.Ok, Points = waypoints };
        _mediator.Send(Arg.Any<GetOsrmRouteQuery>(), Arg.Any<CancellationToken>()).Returns(expected);

        var result = await _controller.OsrmRoute(waypoints, cancellationToken: CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBe(expected);
        await _mediator.Received(1).Send(
            Arg.Is<GetOsrmRouteQuery>(q => ReferenceEquals(q.Coordinates, waypoints)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OsrmRoute_Disabled_IsOkNotNoContent()
    {
        _mediator.Send(Arg.Any<GetOsrmRouteQuery>(), Arg.Any<CancellationToken>())
            .Returns(new RouteGeometryResource { Status = RouteGeometryStatus.Disabled });

        var result = await _controller.OsrmRoute(Waypoints(), cancellationToken: CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>()
            .Value.ShouldBeOfType<RouteGeometryResource>().Status.ShouldBe(RouteGeometryStatus.Disabled);
    }

    [Test]
    public async Task OsrmRoute_PassesTheTransportModeToTheQuery()
    {
        _mediator.Send(Arg.Any<GetOsrmRouteQuery>(), Arg.Any<CancellationToken>())
            .Returns(new RouteGeometryResource { Status = RouteGeometryStatus.Ok });

        await _controller.OsrmRoute(Waypoints(), ContainerTransportMode.ByFoot, CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<GetOsrmRouteQuery>(q => q.TransportMode == ContainerTransportMode.ByFoot),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Directions_WithoutTransportMode_RoutesByCar()
    {
        var waypoints = Waypoints();
        _mediator.Send(Arg.Any<GetRouteQuery>(), Arg.Any<CancellationToken>()).Returns(waypoints);

        var result = await _controller.Directions(waypoints, cancellationToken: CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBe(waypoints);
        await _mediator.Received(1).Send(
            Arg.Is<GetRouteQuery>(q => ReferenceEquals(q.Coordinates, waypoints) && q.TransportMode == ContainerTransportMode.ByCar),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Directions_PassesTheTransportModeToTheQuery()
    {
        _mediator.Send(Arg.Any<GetRouteQuery>(), Arg.Any<CancellationToken>()).Returns(Waypoints());

        await _controller.Directions(Waypoints(), ContainerTransportMode.ByBicycle, CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<GetRouteQuery>(q => q.TransportMode == ContainerTransportMode.ByBicycle),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Directions_NoRoute_IsNoContent()
    {
        _mediator.Send(Arg.Any<GetRouteQuery>(), Arg.Any<CancellationToken>()).Returns((List<RoutePointResource>?)null);

        var result = await _controller.Directions(Waypoints(), cancellationToken: CancellationToken.None);

        result.Result.ShouldBeOfType<NoContentResult>();
    }

    [Test]
    public void OsrmRoute_IsRateLimitedAndHasNoRoleRestriction()
    {
        var method = typeof(RoutingController).GetMethod(nameof(RoutingController.OsrmRoute))!;
        method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false).ShouldBeEmpty();
        method.GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: false)
            .Cast<EnableRateLimitingAttribute>()
            .ShouldHaveSingleItem()
            .PolicyName.ShouldBe(RateLimitingPolicies.GeoProxy);
    }
}
