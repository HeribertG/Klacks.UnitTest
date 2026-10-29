// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for GetOsrmRouteQueryHandler: hands the waypoints to IOsrmRouteGeometryService in order, maps the geometry
/// back to lat/lon resources and pins the JSON contract (status as string, points always an array).
/// </summary>

using System.Text.Json;
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
public class GetOsrmRouteQueryHandlerTests
{
    private static readonly JsonSerializerOptions ApiJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private IOsrmRouteGeometryService _geometryService = null!;
    private GetOsrmRouteQueryHandler _handler = null!;

    private static List<RoutePointResource> Waypoints() =>
    [
        new RoutePointResource { Lat = 47.1, Lon = 8.2 },
        new RoutePointResource { Lat = 47.2, Lon = 8.3 },
    ];

    [SetUp]
    public void SetUp()
    {
        _geometryService = Substitute.For<IOsrmRouteGeometryService>();
        _handler = new GetOsrmRouteQueryHandler(_geometryService);
    }

    [Test]
    public async Task Handle_Ok_MapsGeometryAndPassesWaypointsInOrder()
    {
        IReadOnlyList<RoutePoint>? passed = null;
        _geometryService.GetRouteGeometryAsync(
                Arg.Do<IReadOnlyList<RoutePoint>>(w => passed = w), ContainerTransportMode.ByCar, Arg.Any<CancellationToken>())
            .Returns(new RouteGeometryResult(RouteGeometryStatus.Ok, [new RoutePoint(47.1, 8.2), new RoutePoint(47.15, 8.25), new RoutePoint(47.2, 8.3)]));

        var result = await _handler.Handle(new GetOsrmRouteQuery(Waypoints()), CancellationToken.None);

        passed.ShouldNotBeNull();
        passed.ShouldBe([new RoutePoint(47.1, 8.2), new RoutePoint(47.2, 8.3)]);
        result.Status.ShouldBe(RouteGeometryStatus.Ok);
        result.Points.Count.ShouldBe(3);
        result.Points[1].Lat.ShouldBe(47.15);
        result.Points[1].Lon.ShouldBe(8.25);
    }

    [TestCase(ContainerTransportMode.ByBicycle)]
    [TestCase(ContainerTransportMode.ByFoot)]
    public async Task Handle_PassesTheTransportModeOn(ContainerTransportMode transportMode)
    {
        _geometryService.GetRouteGeometryAsync(Arg.Any<IReadOnlyList<RoutePoint>>(), transportMode, Arg.Any<CancellationToken>())
            .Returns(new RouteGeometryResult(RouteGeometryStatus.Ok, [new RoutePoint(47.1, 8.2)]));

        var result = await _handler.Handle(new GetOsrmRouteQuery(Waypoints(), transportMode), CancellationToken.None);

        result.Status.ShouldBe(RouteGeometryStatus.Ok);
        await _geometryService.Received(1).GetRouteGeometryAsync(
            Arg.Any<IReadOnlyList<RoutePoint>>(), transportMode, Arg.Any<CancellationToken>());
    }

    [TestCase(RouteGeometryStatus.NoRoute)]
    [TestCase(RouteGeometryStatus.Disabled)]
    [TestCase(RouteGeometryStatus.Unavailable)]
    public async Task Handle_NonOkStatus_ReturnsEmptyPoints(RouteGeometryStatus status)
    {
        _geometryService.GetRouteGeometryAsync(Arg.Any<IReadOnlyList<RoutePoint>>(), Arg.Any<ContainerTransportMode>(), Arg.Any<CancellationToken>())
            .Returns(new RouteGeometryResult(status, []));

        var result = await _handler.Handle(new GetOsrmRouteQuery(Waypoints()), CancellationToken.None);

        result.Status.ShouldBe(status);
        result.Points.ShouldBeEmpty();
    }

    [Test]
    public async Task Serialized_MatchesUiContract()
    {
        _geometryService.GetRouteGeometryAsync(Arg.Any<IReadOnlyList<RoutePoint>>(), Arg.Any<ContainerTransportMode>(), Arg.Any<CancellationToken>())
            .Returns(new RouteGeometryResult(RouteGeometryStatus.Ok, [new RoutePoint(47.1, 8.2)]));

        var result = await _handler.Handle(new GetOsrmRouteQuery(Waypoints()), CancellationToken.None);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, ApiJsonOptions));
        var root = document.RootElement;

        root.EnumerateObject().Select(p => p.Name).ShouldBe(["status", "points"]);
        root.GetProperty("status").GetString().ShouldBe("Ok");
        var point = root.GetProperty("points")[0];
        point.EnumerateObject().Select(p => p.Name).ShouldBe(["lat", "lon"]);
        point.GetProperty("lat").GetDouble().ShouldBe(47.1);
        point.GetProperty("lon").GetDouble().ShouldBe(8.2);
    }

    [Test]
    public async Task Serialized_Disabled_HasEmptyPointsArray()
    {
        _geometryService.GetRouteGeometryAsync(Arg.Any<IReadOnlyList<RoutePoint>>(), Arg.Any<ContainerTransportMode>(), Arg.Any<CancellationToken>())
            .Returns(new RouteGeometryResult(RouteGeometryStatus.Disabled, []));

        var result = await _handler.Handle(new GetOsrmRouteQuery(Waypoints()), CancellationToken.None);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, ApiJsonOptions));

        document.RootElement.GetProperty("status").GetString().ShouldBe("Disabled");
        document.RootElement.GetProperty("points").ValueKind.ShouldBe(JsonValueKind.Array);
        document.RootElement.GetProperty("points").GetArrayLength().ShouldBe(0);
    }
}
