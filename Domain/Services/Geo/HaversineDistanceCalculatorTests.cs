// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the Haversine great-circle distance: identity (same point is zero), symmetry, and a
/// known real-world distance (Zürich to Bern is roughly 95 km).
/// </summary>

using Klacks.Api.Domain.Services.Geo;

namespace Klacks.UnitTest.Domain.Services.Geo;

[TestFixture]
public class HaversineDistanceCalculatorTests
{
    private const double ZurichLat = 47.3769;
    private const double ZurichLon = 8.5417;
    private const double BernLat = 46.9480;
    private const double BernLon = 7.4474;

    [Test]
    public void DistanceKm_SamePoint_IsZero()
    {
        var distance = HaversineDistanceCalculator.DistanceKm(ZurichLat, ZurichLon, ZurichLat, ZurichLon);

        distance.ShouldBe(0.0, 0.0001);
    }

    [Test]
    public void DistanceKm_ZurichToBern_IsAboutNinetyFiveKm()
    {
        var distance = HaversineDistanceCalculator.DistanceKm(ZurichLat, ZurichLon, BernLat, BernLon);

        distance.ShouldBeInRange(90.0, 100.0);
    }

    [Test]
    public void DistanceKm_IsSymmetric()
    {
        var forward = HaversineDistanceCalculator.DistanceKm(ZurichLat, ZurichLon, BernLat, BernLon);
        var backward = HaversineDistanceCalculator.DistanceKm(BernLat, BernLon, ZurichLat, ZurichLon);

        forward.ShouldBe(backward, 0.0001);
    }
}
