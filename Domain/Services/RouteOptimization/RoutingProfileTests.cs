// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Services.RouteOptimization;

namespace Klacks.UnitTest.Domain.Services.RouteOptimization;

/// <summary>
/// RoutingProfile: each item transport mode travels with its OSRM profile, a car with driving.
/// </summary>
[TestFixture]
public class RoutingProfileTests
{
    [TestCase(TransportMode.ByCar, "driving")]
    [TestCase(TransportMode.ByBicycle, "cycling")]
    [TestCase(TransportMode.ByFoot, "foot")]
    public void FromTransportMode(TransportMode transportMode, string expected) =>
        RoutingProfile.FromTransportMode(transportMode).ShouldBe(expected);
}
