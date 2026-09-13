// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Services.Geo;

namespace Klacks.UnitTest.Domain.Services.Geo;

[TestFixture]
public class AddressClusterPlannerTests
{
    private const int DefaultSharePercent = 10;

    [Test]
    public void Plan_CityAtOrAboveShare_BecomesClusterCenter()
    {
        var addresses = Enumerable.Range(0, 9).Select(_ => Addr("ZH", "Zürich", 47.37, 8.54))
            .Append(Addr("ZH", "Uster", 47.35, 8.72))
            .ToList();

        var plan = AddressClusterPlanner.Plan(addresses, DefaultSharePercent);

        plan.Clusters.Count.ShouldBe(2);
        plan.Clusters.ShouldContain(c => c.City == "Zürich" && c.DirectCount == 9 && c.AttachedCount == 0);
        plan.Clusters.ShouldContain(c => c.City == "Uster" && c.DirectCount == 1 && c.AttachedCount == 0);
        plan.Rejections.ShouldBeEmpty();
    }

    [Test]
    public void Plan_CityBelowShare_IsAttachedToNearestCenterInSameState()
    {
        var addresses = Enumerable.Range(0, 20).Select(_ => Addr("ZH", "Zürich", 47.37, 8.54))
            .Concat(Enumerable.Range(0, 20).Select(_ => Addr("ZH", "Winterthur", 47.50, 8.72)))
            .Append(Addr("ZH", "Uster", 47.35, 8.72))
            .ToList();
        var uster = addresses[^1];

        var plan = AddressClusterPlanner.Plan(addresses, DefaultSharePercent);

        plan.Clusters.Select(c => c.City).ShouldBe(["Winterthur", "Zürich"]);
        var assignment = plan.Assignments.Single(a => a.ClientId == uster.ClientId);
        assignment.City.ShouldBe("Zürich");
        assignment.AttachedByDistance.ShouldBeTrue();
        assignment.DistanceKm!.Value.ShouldBeGreaterThan(0);
        plan.Clusters.Single(c => c.City == "Zürich").AttachedCount.ShouldBe(1);
    }

    [Test]
    public void Plan_LargestCityOfState_IsAlwaysACenterEvenBelowShare()
    {
        var addresses = new List<ClusterAddress>();
        for (var i = 0; i < 30; i++)
        {
            addresses.Add(Addr("BE", "Ort" + i, 46.9 + i * 0.001, 7.4));
        }

        addresses.Add(Addr("BE", "Ort0", 46.9, 7.4));

        var plan = AddressClusterPlanner.Plan(addresses, DefaultSharePercent);

        plan.Clusters.Count.ShouldBe(1);
        plan.Clusters[0].City.ShouldBe("Ort0");
        plan.Clusters[0].DirectCount.ShouldBe(2);
        plan.Clusters[0].AttachedCount.ShouldBe(29);
    }

    [Test]
    public void Plan_AttachmentNeverCrossesStateBorder()
    {
        var addresses = Enumerable.Range(0, 5).Select(_ => Addr("ZH", "Zürich", 47.37, 8.54))
            .Concat(Enumerable.Range(0, 5).Select(_ => Addr("AG", "Aarau", 47.39, 8.04)))
            .Append(Addr("AG", "Baden", 47.47, 8.31))
            .Append(Addr("AG", "Baden", 47.47, 8.31))
            .Append(Addr("AG", "Baden", 47.47, 8.31))
            .Append(Addr("AG", "Zurzach", 47.59, 8.29))
            .ToList();
        var zurzach = addresses[^1];

        var plan = AddressClusterPlanner.Plan(addresses, 40);

        plan.Assignments.Single(a => a.ClientId == zurzach.ClientId).City.ShouldBe("Aarau");
        plan.Assignments.Single(a => a.ClientId == zurzach.ClientId).State.ShouldBe("AG");
    }

    [Test]
    public void Plan_CenterCoordinates_AreTheMeanOfItsAddresses()
    {
        var addresses = new List<ClusterAddress>
        {
            Addr("ZH", "Zürich", 47.0, 8.0),
            Addr("ZH", "Zürich", 48.0, 9.0),
            Addr("ZH", "Zürich", null, null)
        };

        var plan = AddressClusterPlanner.Plan(addresses, DefaultSharePercent);

        var center = plan.Clusters.Single();
        center.Latitude!.Value.ShouldBe(47.5, 0.000001);
        center.Longitude!.Value.ShouldBe(8.5, 0.000001);
        center.DirectCount.ShouldBe(3);
    }

    [Test]
    public void Plan_SmallTownAddressWithoutCoordinates_IsRejected()
    {
        var addresses = Enumerable.Range(0, 20).Select(_ => Addr("ZH", "Zürich", 47.37, 8.54))
            .Append(Addr("ZH", "Uster", null, null))
            .ToList();
        var uster = addresses[^1];

        var plan = AddressClusterPlanner.Plan(addresses, DefaultSharePercent);

        plan.Rejections.Single().ClientId.ShouldBe(uster.ClientId);
        plan.Rejections.Single().Reason.ShouldBe(AddressClusterPlanner.ReasonNoCoordinates);
    }

    [Test]
    public void Plan_SmallTownButNoCenterWithCoordinates_IsRejected()
    {
        var addresses = Enumerable.Range(0, 20).Select(_ => Addr("ZH", "Zürich", null, null))
            .Concat(Enumerable.Range(0, 20).Select(_ => Addr("ZH", "Winterthur", null, null)))
            .Append(Addr("ZH", "Uster", 47.35, 8.72))
            .ToList();
        var uster = addresses[^1];

        var plan = AddressClusterPlanner.Plan(addresses, DefaultSharePercent);

        plan.Rejections.Single().ClientId.ShouldBe(uster.ClientId);
        plan.Rejections.Single().Reason.ShouldBe(AddressClusterPlanner.ReasonNoCenterCoordinates);
    }

    [Test]
    public void Plan_SingleCenterWithoutCoordinates_AttachesSmallTownsToIt()
    {
        var addresses = Enumerable.Range(0, 20).Select(_ => Addr("ZH", "Zürich", null, null))
            .Append(Addr("ZH", "Uster", 47.35, 8.72))
            .ToList();
        var uster = addresses[^1];

        var plan = AddressClusterPlanner.Plan(addresses, DefaultSharePercent);

        plan.Rejections.ShouldBeEmpty();
        var assignment = plan.Assignments.Single(a => a.ClientId == uster.ClientId);
        assignment.City.ShouldBe("Zürich");
        assignment.AttachedByDistance.ShouldBeTrue();
        assignment.DistanceKm.ShouldBeNull();
        plan.Clusters.Single(c => c.City == "Zürich").AttachedCount.ShouldBe(1);
    }

    [Test]
    public void Plan_CityNamesAreMatchedCaseInsensitivelyAndTrimmed()
    {
        var addresses = new List<ClusterAddress>
        {
            Addr("ZH", "Zürich", 47.37, 8.54),
            Addr("ZH", " zürich ", 47.37, 8.54),
            Addr("ZH", "ZÜRICH", 47.37, 8.54)
        };

        var plan = AddressClusterPlanner.Plan(addresses, DefaultSharePercent);

        plan.Clusters.Single().City.ShouldBe("Zürich");
        plan.Clusters.Single().DirectCount.ShouldBe(3);
    }

    [Test]
    public void Plan_IsDeterministic_ClustersSortedByCountryStateCity()
    {
        var addresses = new List<ClusterAddress>
        {
            Addr("ZH", "Zürich", 47.37, 8.54, country: "CH"),
            Addr("BE", "Bern", 46.95, 7.45, country: "CH"),
            Addr("BY", "München", 48.14, 11.58, country: "DE")
        };

        var plan = AddressClusterPlanner.Plan(addresses, DefaultSharePercent);

        plan.Clusters.Select(c => $"{c.Country}/{c.State}/{c.City}").ShouldBe(["CH/BE/Bern", "CH/ZH/Zürich", "DE/BY/München"]);
    }

    [TestCase(0)]
    [TestCase(101)]
    public void Plan_ShareOutsideOneToHundred_Throws(int sharePercent)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => AddressClusterPlanner.Plan([Addr("ZH", "Zürich", 47.37, 8.54)], sharePercent));
    }

    [Test]
    public void Plan_EveryAddressIsEitherAssignedOrRejected_NeverBoth()
    {
        var addresses = Enumerable.Range(0, 20).Select(_ => Addr("ZH", "Zürich", 47.37, 8.54))
            .Append(Addr("ZH", "Uster", null, null))
            .Append(Addr("ZH", "Wetzikon", 47.32, 8.80))
            .ToList();

        var plan = AddressClusterPlanner.Plan(addresses, DefaultSharePercent);

        (plan.Assignments.Count + plan.Rejections.Count).ShouldBe(addresses.Count);
    }

    [Test]
    public void Plan_EmptyInput_ReturnsEmptyPlan()
    {
        var plan = AddressClusterPlanner.Plan([], DefaultSharePercent);

        plan.Clusters.ShouldBeEmpty();
        plan.Assignments.ShouldBeEmpty();
        plan.Rejections.ShouldBeEmpty();
    }

    [Test]
    public void Plan_NullAddresses_Throws()
    {
        Should.Throw<ArgumentNullException>(() => AddressClusterPlanner.Plan(null!, DefaultSharePercent));
    }

    private static ClusterAddress Addr(string state, string city, double? latitude, double? longitude, string country = "CH") =>
        new(Guid.NewGuid(), country, state, city, latitude, longitude);
}
