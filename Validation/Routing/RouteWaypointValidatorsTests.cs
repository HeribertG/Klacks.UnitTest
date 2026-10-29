// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the shared route waypoint rules: GetRouteQueryValidator (OpenRouteService directions) and
/// GetOsrmRouteQueryValidator (OSRM geometry) must accept and reject exactly the same waypoint lists, including a
/// null list, which fails validation instead of throwing.
/// </summary>

using Klacks.Api.Application.DTOs.Routing;
using Klacks.Api.Application.Queries.Routing;
using Klacks.Api.Application.Validation.Routing;
using Klacks.Api.Domain.Enums;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Validation.Routing;

[TestFixture]
public class RouteWaypointValidatorsTests
{
    private static readonly Func<List<RoutePointResource>, bool>[] Validators =
    [
        coordinates => new GetRouteQueryValidator().Validate(new GetRouteQuery(coordinates)).IsValid,
        coordinates => new GetOsrmRouteQueryValidator().Validate(new GetOsrmRouteQuery(coordinates)).IsValid,
    ];

    private static List<RoutePointResource> Points(int count, double lat = 47.0, double lon = 8.0) =>
        Enumerable.Range(0, count).Select(_ => new RoutePointResource { Lat = lat, Lon = lon }).ToList();

    private static IEnumerable<TestCaseData> Cases()
    {
        yield return new TestCaseData(Points(RouteWaypointRules.MinimumWaypoints), true).SetName("two waypoints");
        yield return new TestCaseData(Points(RouteWaypointRules.MaximumWaypoints), true).SetName("fifty waypoints");
        yield return new TestCaseData(Points(0), false).SetName("no waypoint");
        yield return new TestCaseData(Points(1), false).SetName("one waypoint");
        yield return new TestCaseData(Points(RouteWaypointRules.MaximumWaypoints + 1), false).SetName("fifty-one waypoints");
        yield return new TestCaseData(Points(2, lat: 90.0, lon: 180.0), true).SetName("upper bounds");
        yield return new TestCaseData(Points(2, lat: -90.0, lon: -180.0), true).SetName("lower bounds");
        yield return new TestCaseData(Points(2, lat: 90.1), false).SetName("latitude out of range");
        yield return new TestCaseData(Points(2, lon: -180.1), false).SetName("longitude out of range");
        yield return new TestCaseData(null, false).SetName("null list");
    }

    [TestCaseSource(nameof(Cases))]
    public void BothValidators_AgreeOnWaypointRules(List<RoutePointResource>? coordinates, bool expectedValid)
    {
        foreach (var validate in Validators)
        {
            validate(coordinates!).ShouldBe(expectedValid);
        }
    }

    [TestCase(ContainerTransportMode.ByCar, true)]
    [TestCase(ContainerTransportMode.Mix, true)]
    [TestCase((ContainerTransportMode)7, false)]
    public void BothValidators_AcceptOnlyDefinedTransportModes(ContainerTransportMode transportMode, bool expectedValid)
    {
        var coordinates = Points(RouteWaypointRules.MinimumWaypoints);

        new GetRouteQueryValidator().Validate(new GetRouteQuery(coordinates, transportMode)).IsValid.ShouldBe(expectedValid);
        new GetOsrmRouteQueryValidator().Validate(new GetOsrmRouteQuery(coordinates, transportMode)).IsValid.ShouldBe(expectedValid);
    }

    [Test]
    public void GetRouteQueryValidator_KeepsExistingMessages()
    {
        var tooFew = new GetRouteQueryValidator().Validate(new GetRouteQuery(Points(1)));
        var outOfRange = new GetRouteQueryValidator().Validate(new GetRouteQuery(Points(2, lat: 91)));

        tooFew.Errors.ShouldContain(e => e.ErrorMessage == "A route needs at least 2 waypoints.");
        outOfRange.Errors.ShouldContain(e => e.ErrorMessage == "Latitude is out of range.");
        outOfRange.Errors.ShouldContain(e => e.PropertyName == "Coordinates[0].Lat");
    }
}
