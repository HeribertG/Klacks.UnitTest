// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// GeographicClusterer is deterministic and pure: the same points give the same clusters whatever the input order, separated
/// groups of points end up in their own cluster, no cluster is ever empty (also for identical coordinates), every point lands
/// in exactly one cluster, the clusters are ordered from west to east and invalid input is refused.
/// </summary>

using Klacks.Api.Domain.Services.Geo;

namespace Klacks.UnitTest.Domain.Services.Geo;

[TestFixture]
public class GeographicClustererTests
{
    private const double WinterthurLat = 47.50;
    private const double WinterthurLon = 8.72;
    private const double BernLat = 46.95;
    private const double BernLon = 7.45;
    private const double LuganoLat = 46.00;
    private const double LuganoLon = 8.95;
    private const double NearbyOffset = 0.01;

    private static GeographicClusterPoint Point(int seed, double lat, double lon) =>
        new(new Guid(seed, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]), lat, lon);

    private static List<GeographicClusterPoint> Blob(int firstSeed, double lat, double lon, int size) =>
        Enumerable.Range(0, size)
            .Select(i => Point(firstSeed + i, lat + (i * NearbyOffset), lon + (i * NearbyOffset)))
            .ToList();

    private static string Describe(IReadOnlyList<GeographicCluster> clusters) =>
        string.Join("|", clusters.Select(c => $"{c.Index}:{string.Join(",", c.MemberIds)}"));

    [Test]
    public void TwoSeparatedGroups_AreSplitIntoThemselves()
    {
        var east = Blob(1, WinterthurLat, WinterthurLon, 5);
        var west = Blob(100, BernLat, BernLon, 4);

        var clusters = GeographicClusterer.Cluster(east.Concat(west).ToList(), 2);

        clusters.Count.ShouldBe(2);
        clusters[0].MemberIds.ShouldBe(west.Select(p => p.Id).OrderBy(id => id));
        clusters[1].MemberIds.ShouldBe(east.Select(p => p.Id).OrderBy(id => id));
    }

    [Test]
    public void ThreeSeparatedGroups_AreSplitIntoThemselves_OrderedWestToEast()
    {
        var bern = Blob(1, BernLat, BernLon, 3);
        var winterthur = Blob(50, WinterthurLat, WinterthurLon, 6);
        var lugano = Blob(100, LuganoLat, LuganoLon, 2);

        var clusters = GeographicClusterer.Cluster(lugano.Concat(winterthur).Concat(bern).ToList(), 3);

        clusters.Select(c => c.Index).ShouldBe([0, 1, 2]);
        clusters[0].MemberIds.ShouldBe(bern.Select(p => p.Id).OrderBy(id => id));
        clusters[1].MemberIds.ShouldBe(winterthur.Select(p => p.Id).OrderBy(id => id));
        clusters[2].MemberIds.ShouldBe(lugano.Select(p => p.Id).OrderBy(id => id));
        clusters[0].CentroidLongitude.ShouldBeLessThan(clusters[1].CentroidLongitude);
        clusters[1].CentroidLongitude.ShouldBeLessThan(clusters[2].CentroidLongitude);
    }

    [Test]
    public void TheResult_DoesNotDependOnTheOrderOfTheInput()
    {
        var points = Blob(1, WinterthurLat, WinterthurLon, 7)
            .Concat(Blob(100, BernLat, BernLon, 6))
            .Concat(Blob(200, LuganoLat, LuganoLon, 5))
            .ToList();
        var expected = Describe(GeographicClusterer.Cluster(points, 3));

        var random = new Random(7);
        for (var run = 0; run < 12; run++)
        {
            var shuffled = points.OrderBy(_ => random.Next()).ToList();
            Describe(GeographicClusterer.Cluster(shuffled, 3)).ShouldBe(expected);
        }
    }

    [Test]
    public void TheSameInput_GivesTheSameResultOnEveryRun()
    {
        var points = Blob(1, WinterthurLat, WinterthurLon, 9).Concat(Blob(100, BernLat, BernLon, 9)).ToList();

        var first = Describe(GeographicClusterer.Cluster(points, 4));

        for (var run = 0; run < 5; run++)
        {
            Describe(GeographicClusterer.Cluster(points, 4)).ShouldBe(first);
        }
    }

    [Test]
    public void EveryPoint_LandsInExactlyOneNonEmptyCluster()
    {
        var points = Blob(1, WinterthurLat, WinterthurLon, 8)
            .Concat(Blob(100, BernLat, BernLon, 7))
            .Concat(Blob(200, LuganoLat, LuganoLon, 3))
            .ToList();

        var clusters = GeographicClusterer.Cluster(points, 5);

        clusters.ShouldAllBe(c => c.MemberIds.Count > 0);
        clusters.SelectMany(c => c.MemberIds).OrderBy(id => id).ShouldBe(points.Select(p => p.Id).OrderBy(id => id));
    }

    [Test]
    public void IdenticalCoordinates_NeverLeaveAClusterEmpty()
    {
        var points = Enumerable.Range(1, 6).Select(i => Point(i, WinterthurLat, WinterthurLon)).ToList();

        var clusters = GeographicClusterer.Cluster(points, 3);

        clusters.Count.ShouldBe(3);
        clusters.ShouldAllBe(c => c.MemberIds.Count > 0);
        clusters.Sum(c => c.MemberIds.Count).ShouldBe(6);
    }

    [Test]
    public void OneCluster_HoldsEveryPoint()
    {
        var points = Blob(1, WinterthurLat, WinterthurLon, 4).Concat(Blob(100, BernLat, BernLon, 4)).ToList();

        var clusters = GeographicClusterer.Cluster(points, 1);

        clusters.Count.ShouldBe(1);
        clusters[0].MemberIds.Count.ShouldBe(8);
    }

    [Test]
    public void AsManyClustersAsPoints_GivesEachPointItsOwnCluster()
    {
        var points = Blob(1, WinterthurLat, WinterthurLon, 3).Concat(Blob(100, BernLat, BernLon, 2)).ToList();

        var clusters = GeographicClusterer.Cluster(points, 5);

        clusters.ShouldAllBe(c => c.MemberIds.Count == 1);
    }

    [Test]
    public void MoreClustersThanPoints_IsRefused()
    {
        var points = Blob(1, WinterthurLat, WinterthurLon, 2);

        Should.Throw<ArgumentOutOfRangeException>(() => GeographicClusterer.Cluster(points, 3));
    }

    [Test]
    public void AClusterCountBelowOne_IsRefused()
    {
        var points = Blob(1, WinterthurLat, WinterthurLon, 2);

        Should.Throw<ArgumentOutOfRangeException>(() => GeographicClusterer.Cluster(points, 0));
    }

    [Test]
    public void DuplicateIds_AreRefused()
    {
        var point = Point(1, WinterthurLat, WinterthurLon);

        Should.Throw<ArgumentException>(() => GeographicClusterer.Cluster([point, point], 2));
    }
}
