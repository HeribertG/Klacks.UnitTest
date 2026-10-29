// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.RouteOptimization;

namespace Klacks.UnitTest.Domain.Services.RouteOptimization;

/// <summary>
/// RoutingProfile: each item transport mode travels with its OSRM profile, a car with driving; a container transport
/// mode maps to one OSRM and one OpenRouteService profile (mix as car) and to the matching ORS GeoJSON directions path.
/// </summary>
[TestFixture]
public class RoutingProfileTests
{
    [TestCase(TransportMode.ByCar, "driving")]
    [TestCase(TransportMode.ByBicycle, "cycling")]
    [TestCase(TransportMode.ByFoot, "foot")]
    public void FromTransportMode(TransportMode transportMode, string expected) =>
        RoutingProfile.FromTransportMode(transportMode).ShouldBe(expected);

    [TestCase(ContainerTransportMode.ByCar, "driving")]
    [TestCase(ContainerTransportMode.ByBicycle, "cycling")]
    [TestCase(ContainerTransportMode.ByFoot, "foot")]
    [TestCase(ContainerTransportMode.Mix, "driving")]
    public void FromContainerTransportMode(ContainerTransportMode transportMode, string expected) =>
        RoutingProfile.FromContainerTransportMode(transportMode).ShouldBe(expected);

    [TestCase(ContainerTransportMode.ByCar, "driving-car")]
    [TestCase(ContainerTransportMode.ByBicycle, "cycling-regular")]
    [TestCase(ContainerTransportMode.ByFoot, "foot-walking")]
    [TestCase(ContainerTransportMode.Mix, "driving-car")]
    public void OpenRouteServiceProfile(ContainerTransportMode transportMode, string expected) =>
        RoutingProfile.OpenRouteServiceProfile(transportMode).ShouldBe(expected);

    [TestCase(ContainerTransportMode.ByCar)]
    [TestCase(ContainerTransportMode.ByBicycle)]
    [TestCase(ContainerTransportMode.ByFoot)]
    [TestCase(ContainerTransportMode.Mix)]
    public void OpenRouteServiceDirectionsGeoJsonPath_IsTheDirectionsPathOfTheProfile(ContainerTransportMode transportMode) =>
        RoutingProfile.OpenRouteServiceDirectionsGeoJsonPath(transportMode)
            .ShouldBe($"/directions/{RoutingProfile.OpenRouteServiceProfile(transportMode)}/geojson");

    [Test]
    public void GeoJsonPathConstants_NameTheirProfile()
    {
        GeoServicePaths.OpenRouteServiceDirectionsDrivingCarGeoJson.ShouldBe("/directions/driving-car/geojson");
        GeoServicePaths.OpenRouteServiceDirectionsCyclingRegularGeoJson.ShouldBe("/directions/cycling-regular/geojson");
        GeoServicePaths.OpenRouteServiceDirectionsFootWalkingGeoJson.ShouldBe("/directions/foot-walking/geojson");
    }
}
