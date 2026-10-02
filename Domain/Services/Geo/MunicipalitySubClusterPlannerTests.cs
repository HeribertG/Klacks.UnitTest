// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Services.Geo;

namespace Klacks.UnitTest.Domain.Services.Geo;

[TestFixture]
public class MunicipalitySubClusterPlannerTests
{
    private const string Country = "CH";
    private const string State = "ZH";
    private const string Winterthur = "Winterthur";
    private const string Seuzach = "Seuzach";
    private const string Wiesendangen = "Wiesendangen";
    private const string Elsau = "Elsau";
    private const string Hettlingen = "Hettlingen";
    private const string Wuelflingen = "Wülflingen";
    private const int DefaultShare = 15;

    private static readonly (double Lat, double Lon) WinterthurPos = (47.4999, 8.7262);
    private static readonly (double Lat, double Lon) SeuzachPos = (47.5363, 8.7322);
    private static readonly (double Lat, double Lon) WiesendangenPos = (47.5216, 8.7898);
    private static readonly (double Lat, double Lon) ElsauPos = (47.5033, 8.7972);
    private static readonly (double Lat, double Lon) HettlingenPos = (47.5466, 8.7067);
    private static readonly (double Lat, double Lon) WuelflingenPos = (47.5100, 8.6900);

    [Test]
    public void Plan_PlaceAboveShareAndMinimum_BecomesSubCluster_CentreCityStaysInParent()
    {
        var members = Many(Winterthur, WinterthurPos, 14)
            .Concat(Many(Seuzach, SeuzachPos, 5))
            .Concat(Many(Wiesendangen, WiesendangenPos, 5))
            .ToList();

        var plan = Plan(members);

        plan.SubClusters.Select(s => s.City).ShouldBe([Seuzach, Wiesendangen]);
        plan.SubClusters.ShouldAllBe(s => s.DirectCount == 5 && s.AttachedCount == 0);
        plan.SubClusters.ShouldNotContain(s => s.City == Winterthur);
        plan.SubClusterByClientId.Count.ShouldBe(10);
        members.Where(m => m.City == Winterthur).ShouldAllBe(m => !plan.SubClusterByClientId.ContainsKey(m.ClientId));
    }

    [Test]
    public void Plan_SubClusterCentre_IsMeanOfItsAddresses()
    {
        var members = Many(Winterthur, WinterthurPos, 5).ToList();
        members.Add(Address(Seuzach, (47.53, 8.73)));
        members.Add(Address(Seuzach, (47.54, 8.74)));
        members.Add(Address(Seuzach, (47.55, 8.75)));

        var plan = Plan(members);

        var seuzach = plan.SubClusters.ShouldHaveSingleItem();
        seuzach.Latitude!.Value.ShouldBe(47.54, 1e-9);
        seuzach.Longitude!.Value.ShouldBe(8.74, 1e-9);
    }

    [Test]
    public void Plan_SmallPlaceNearerToSubClusterThanCentre_JoinsNearestSubCluster()
    {
        var members = Many(Winterthur, WinterthurPos, 14)
            .Concat(Many(Seuzach, SeuzachPos, 5))
            .Concat(Many(Wiesendangen, WiesendangenPos, 5))
            .Concat(Many(Elsau, ElsauPos, 3))
            .Concat(Many(Hettlingen, HettlingenPos, 2))
            .ToList();

        var plan = Plan(members);

        plan.SubClusters.Select(s => s.City).ShouldBe([Seuzach, Wiesendangen]);
        plan.Attachments.Single(a => a.Place == Elsau).SubClusterCity.ShouldBe(Wiesendangen);
        plan.Attachments.Single(a => a.Place == Hettlingen).SubClusterCity.ShouldBe(Seuzach);
        plan.SubClusters.Single(s => s.City == Wiesendangen).AttachedCount.ShouldBe(3);
        plan.SubClusters.Single(s => s.City == Seuzach).AttachedCount.ShouldBe(2);
        members.Where(m => m.City == Elsau).ShouldAllBe(m => plan.SubClusterByClientId[m.ClientId] == Wiesendangen);
    }

    [Test]
    public void Plan_SmallPlaceNearerToCentre_StaysInCityCluster()
    {
        var members = Many(Winterthur, WinterthurPos, 14)
            .Concat(Many(Seuzach, SeuzachPos, 5))
            .Concat(Many(Wuelflingen, WuelflingenPos, 2))
            .ToList();

        var plan = Plan(members);

        var attachment = plan.Attachments.Single(a => a.Place == Wuelflingen);
        attachment.SubClusterCity.ShouldBeNull();
        attachment.DistanceKm.ShouldBeNull();
        members.Where(m => m.City == Wuelflingen).ShouldAllBe(m => !plan.SubClusterByClientId.ContainsKey(m.ClientId));
    }

    [Test]
    public void Plan_PlaceBelowShare_ProducesNoSubCluster()
    {
        var members = Many(Winterthur, WinterthurPos, 40)
            .Concat(Many(Seuzach, SeuzachPos, 5))
            .ToList();

        var plan = Plan(members);

        plan.SubClusters.ShouldBeEmpty();
        plan.SubClusterByClientId.ShouldBeEmpty();
        plan.Attachments.ShouldHaveSingleItem().SubClusterCity.ShouldBeNull();
    }

    [Test]
    public void Plan_PlaceAboveShareButBelowMinimumCount_ProducesNoSubCluster()
    {
        var members = Many(Winterthur, WinterthurPos, 2)
            .Concat(Many(Seuzach, SeuzachPos, MunicipalitySubClusterPlanner.MinimumSubClusterMembers - 1))
            .ToList();

        var plan = Plan(members);

        plan.SubClusters.ShouldBeEmpty();
    }

    [Test]
    public void Plan_CentreCityNameDiffersInCasing_IsStillTheCentre()
    {
        var members = Many(Winterthur, WinterthurPos, 3)
            .Concat(Many(Winterthur.ToUpperInvariant(), WinterthurPos, 3))
            .ToList();

        var plan = Plan(members);

        plan.SubClusters.ShouldBeEmpty();
        plan.Attachments.ShouldBeEmpty();
    }

    [Test]
    public void Plan_CentreWithoutCoordinates_SmallPlacesStayInCityCluster()
    {
        var members = Many(Winterthur, WinterthurPos, 14)
            .Concat(Many(Seuzach, SeuzachPos, 5))
            .Concat(Many(Hettlingen, HettlingenPos, 2))
            .ToList();

        var plan = MunicipalitySubClusterPlanner.Plan(Winterthur, null, null, members, DefaultShare);

        plan.SubClusters.ShouldHaveSingleItem();
        plan.Attachments.Single(a => a.Place == Hettlingen).SubClusterCity.ShouldBeNull();
    }

    [Test]
    public void Plan_ShareOutOfRange_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            MunicipalitySubClusterPlanner.Plan(Winterthur, null, null, [], 0));
    }

    private static MunicipalitySubClusterPlan Plan(IReadOnlyList<ClusterAddress> members) =>
        MunicipalitySubClusterPlanner.Plan(Winterthur, WinterthurPos.Lat, WinterthurPos.Lon, members, DefaultShare);

    private static IEnumerable<ClusterAddress> Many(string city, (double Lat, double Lon) position, int count) =>
        Enumerable.Range(0, count).Select(_ => Address(city, position));

    private static ClusterAddress Address(string city, (double Lat, double Lon) position) =>
        new(Guid.NewGuid(), Country, State, city, position.Lat, position.Lon);
}
