// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the pure nearest-anchor assignment: picks the geographically closest anchor, returns
/// the great-circle distance, and yields null when there are no anchors to choose from.
/// </summary>

using Klacks.Api.Domain.Services.Geo;

namespace Klacks.UnitTest.Domain.Services.Geo;

[TestFixture]
public class CustomerGroupAssignerTests
{
    private static readonly Guid Zurich = Guid.NewGuid();
    private static readonly Guid Bern = Guid.NewGuid();

    [Test]
    public void FindNearest_PicksClosestAnchor()
    {
        var anchors = new List<GroupAnchor>
        {
            new(Zurich, 47.3769, 8.5417),
            new(Bern, 46.9480, 7.4474)
        };
        var customer = new CustomerLocation(Guid.NewGuid(), 47.0, 7.5);

        var result = CustomerGroupAssigner.FindNearest(customer, anchors);

        result.ShouldNotBeNull();
        result!.GroupId.ShouldBe(Bern);
        result.DistanceKm.ShouldBeGreaterThan(0.0);
    }

    [Test]
    public void FindNearest_NoAnchors_ReturnsNull()
    {
        var customer = new CustomerLocation(Guid.NewGuid(), 47.0, 7.5);

        var result = CustomerGroupAssigner.FindNearest(customer, new List<GroupAnchor>());

        result.ShouldBeNull();
    }

    [Test]
    public void FindNearest_AnchorAtCustomerLocation_DistanceIsZero()
    {
        var anchors = new List<GroupAnchor> { new(Zurich, 47.3769, 8.5417) };
        var customer = new CustomerLocation(Guid.NewGuid(), 47.3769, 8.5417);

        var result = CustomerGroupAssigner.FindNearest(customer, anchors);

        result.ShouldNotBeNull();
        result!.GroupId.ShouldBe(Zurich);
        result.DistanceKm.ShouldBe(0.0, 0.0001);
    }
}
