// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ShiftCityGroupPlanner on a tree shaped like the live one (region → state → city):
/// only city groups (leaves) are ever a target, the exact city name wins, a group name that merely
/// contains the city is found inside the customer's state, a state with a single city group takes every
/// customer of that state, otherwise the nearest city group of the state wins (located by the mean
/// position of the addresses carrying its name), the replaced links and their validity are carried into
/// the plan, and a shift that already sits in a city group is skipped.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Services.Grouping;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class ShiftCityGroupPlannerTests
{
    private static readonly DateTime Today = new(2099, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    private Group _zurichRegion = null!;
    private Group _aargau = null!;
    private Group _zurichState = null!;
    private Group _uster = null!;
    private Group _winterthur = null!;
    private Group _zurich = null!;
    private Group _westRegion = null!;
    private Group _geneva = null!;
    private Group _genevaCity = null!;
    private Group _vaud = null!;
    private Group _lausanne = null!;
    private Group _eastRegion = null!;
    private Group _lucerne = null!;
    private Group _lucerneCity = null!;
    private Group _emmen = null!;
    private List<Group> _groups = null!;
    private List<CityCentroid> _centroids = null!;

    [SetUp]
    public void Setup()
    {
        _zurichRegion = Node("Deutschschweiz Zürich", null, null, 1, 12);
        _aargau = Node("AG", _zurichRegion, _zurichRegion, 2, 3);
        _zurichState = Node("ZH", _zurichRegion, _zurichRegion, 4, 11);
        _uster = Node("Uster", _zurichState, _zurichRegion, 5, 6);
        _winterthur = Node("Winterthur", _zurichState, _zurichRegion, 7, 8);
        _zurich = Node("Zürich", _zurichState, _zurichRegion, 9, 10);

        _westRegion = Node("Westschweiz", null, null, 1, 10);
        _geneva = Node("GE", _westRegion, _westRegion, 2, 5);
        _genevaCity = Node("Genf Stadt", _geneva, _westRegion, 3, 4);
        _vaud = Node("VD", _westRegion, _westRegion, 6, 9);
        _lausanne = Node("Lausanne", _vaud, _westRegion, 7, 8);

        _eastRegion = Node("Deutschschweiz Ost", null, null, 1, 8);
        _lucerne = Node("LU", _eastRegion, _eastRegion, 2, 7);
        _lucerneCity = Node("Luzern Stadt", _lucerne, _eastRegion, 3, 4);
        _emmen = Node("Emmen", _lucerne, _eastRegion, 5, 6);

        _groups =
        [
            _zurichRegion, _aargau, _zurichState, _uster, _winterthur, _zurich,
            _westRegion, _geneva, _genevaCity, _vaud, _lausanne,
            _eastRegion, _lucerne, _lucerneCity, _emmen
        ];

        _centroids =
        [
            new CityCentroid("zürich", 47.37, 8.54, 40),
            new CityCentroid("winterthur", 47.50, 8.72, 10),
            new CityCentroid("uster", 47.35, 8.72, 5),
            new CityCentroid("lausanne", 46.52, 6.63, 12),
            new CityCentroid("luzern", 47.05, 8.31, 20),
            new CityCentroid("emmen", 47.08, 8.30, 3)
        ];
    }

    private static Group Node(string name, Group? parent, Group? root, int lft, int rgt)
    {
        var id = Guid.NewGuid();
        return new Group
        {
            Id = id,
            Name = name,
            Parent = parent?.Id,
            Root = root?.Id ?? id,
            Lft = lft,
            Rgt = rgt
        };
    }

    private static Client Customer(Address address, params GroupItem[] memberships)
    {
        var customerId = Guid.NewGuid();
        address.ClientId = customerId;

        return new Client
        {
            Id = customerId,
            Name = "Muster AG",
            Company = "Muster AG",
            Type = EntityTypeEnum.Customer,
            Addresses = [address],
            GroupItems = memberships.ToList()
        };
    }

    private static Address Addr(string city, string state, double? latitude = null, double? longitude = null) =>
        new()
        {
            Type = AddressTypeEnum.Employee,
            City = city,
            State = state,
            Latitude = latitude,
            Longitude = longitude,
            ValidFrom = Today
        };

    private static GroupItem LinkTo(Group group, DateTime? validFrom = null, DateTime? validUntil = null) =>
        new() { Id = Guid.NewGuid(), GroupId = group.Id, ValidFrom = validFrom, ValidUntil = validUntil };

    private static Shift ShiftFor(Client? customer, params GroupItem[] links) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = "Service",
            Status = ShiftStatus.OriginalShift,
            ClientId = customer?.Id,
            Client = customer,
            GroupItems = links.ToList()
        };

    private ShiftCityGroupPlan Plan(params Shift[] shifts) =>
        ShiftCityGroupPlanner.Plan(shifts, _groups, _centroids, Today);

    [Test]
    public void ExactCityName_WinsEvenWhenTheShiftSitsInAnotherRegion()
    {
        var regionLink = LinkTo(_westRegion);
        var plan = Plan(ShiftFor(Customer(Addr("Winterthur", "ZH")), regionLink));

        plan.Assignments.Count.ShouldBe(1);
        var assignment = plan.Assignments[0];
        assignment.GroupId.ShouldBe(_winterthur.Id);
        assignment.MatchReason.ShouldStartWith("city name (");
        assignment.ReplacedGroupItemIds.ShouldBe([regionLink.Id]);
        assignment.ReplacedGroupNames.ShouldBe(["Westschweiz"]);
    }

    [Test]
    public void GroupNameContainingTheCity_IsFoundInsideTheState()
    {
        var plan = Plan(ShiftFor(Customer(Addr("Luzern", "LU", 47.05, 8.31)), LinkTo(_eastRegion)));

        plan.Assignments.Single().GroupId.ShouldBe(_lucerneCity.Id);
        plan.Assignments.Single().MatchReason.ShouldStartWith("city name contained in the group name");
    }

    [Test]
    public void StateWithASingleCityGroup_TakesTheCustomer_EvenWhenTheCityIsSpelledDifferently()
    {
        var plan = Plan(ShiftFor(Customer(Addr("Genève", "GE")), LinkTo(_zurichRegion)));

        plan.Assignments.Single().GroupId.ShouldBe(_genevaCity.Id);
        plan.Assignments.Single().MatchReason.ShouldStartWith("only city group of the customer's state");
    }

    [Test]
    public void StateThatIsItselfALeaf_IsTheTarget()
    {
        var plan = Plan(ShiftFor(Customer(Addr("Baden", "AG")), LinkTo(_zurichRegion)));

        plan.Assignments.Single().GroupId.ShouldBe(_aargau.Id);
    }

    [Test]
    public void NoNameMatch_PicksTheNearestCityGroupOfTheState_LocatedByAddressCentroids()
    {
        var plan = Plan(ShiftFor(Customer(Addr("Regensdorf", "ZH", 47.43, 8.47)), LinkTo(_zurichRegion)));

        var assignment = plan.Assignments.Single();
        assignment.GroupId.ShouldBe(_zurich.Id);
        assignment.MatchReason.ShouldStartWith("nearest city group in the customer's state");
        assignment.DistanceKm.ShouldNotBeNull();
    }

    [Test]
    public void GroupCoordinates_OutrankTheAddressCentroid()
    {
        _uster.Latitude = 47.43;
        _uster.Longitude = 8.47;

        var plan = Plan(ShiftFor(Customer(Addr("Regensdorf", "ZH", 47.43, 8.47))));

        plan.Assignments.Single().GroupId.ShouldBe(_uster.Id);
    }

    [Test]
    public void UnknownState_FallsBackToTheNearestCityGroupOverall()
    {
        var plan = Plan(ShiftFor(Customer(Addr("Pully", string.Empty, 46.51, 6.66))));

        var assignment = plan.Assignments.Single();
        assignment.GroupId.ShouldBe(_lausanne.Id);
        assignment.MatchReason.ShouldStartWith("nearest city group (");
    }

    [Test]
    public void RegionAndStateNodes_AreNeverATarget()
    {
        var plan = Plan(ShiftFor(Customer(Addr("LU", "LU", 47.08, 8.30))));

        plan.Assignments.Single().GroupId.ShouldNotBe(_lucerne.Id);
    }

    [Test]
    public void ShiftAlreadyInACityGroup_IsSkipped()
    {
        var plan = Plan(ShiftFor(Customer(Addr("Winterthur", "ZH")), LinkTo(_zurich), LinkTo(_zurichRegion)));

        plan.SkippedAlreadyInCityGroupCount.ShouldBe(1);
        plan.Assignments.ShouldBeEmpty();
    }

    [Test]
    public void ActiveCityGroupMembershipOfTheCustomer_Wins()
    {
        var customer = Customer(Addr("Winterthur", "ZH"), LinkTo(_uster));

        var plan = Plan(ShiftFor(customer, LinkTo(_zurichRegion)));

        plan.Assignments.Single().GroupId.ShouldBe(_uster.Id);
        plan.Assignments.Single().MatchReason.ShouldBe("customer's city-group membership");
    }

    [Test]
    public void ValidityOfTheReplacedLinks_IsCarriedOver()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var until = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        var plan = Plan(ShiftFor(Customer(Addr("Zürich", "ZH")), LinkTo(_zurichRegion, from, until)));

        plan.Assignments.Single().ValidFrom.ShouldBe(from);
        plan.Assignments.Single().ValidUntil.ShouldBe(until);
    }

    [Test]
    public void ShiftWithoutAnyGroup_IsPlacedWithoutReplacingAnything()
    {
        var plan = Plan(ShiftFor(Customer(Addr("Uster", "ZH"))));

        var assignment = plan.Assignments.Single();
        assignment.GroupId.ShouldBe(_uster.Id);
        assignment.ReplacedGroupItemIds.ShouldBeEmpty();
        assignment.ValidFrom.ShouldBeNull();
    }

    [Test]
    public void NoNameMatchAndNoCoordinates_StaysUnassigned()
    {
        var link = LinkTo(_zurichRegion);

        var plan = Plan(ShiftFor(Customer(Addr("Regensdorf", "ZH")), link));

        plan.Assignments.ShouldBeEmpty();
        plan.Unassignable.Single().Reason.ShouldContain("has no coordinates");
    }

    [Test]
    public void ShiftWithoutCustomer_StaysUnassigned()
    {
        var plan = Plan(ShiftFor(null, LinkTo(_zurichRegion)));

        plan.Unassignable.Single().Reason.ShouldBe("the shift has no customer");
    }

    [Test]
    public void MaxCount_CountsOnlyShiftsThatStillNeedToMove()
    {
        var alreadyPlaced = Enumerable.Range(0, 3)
            .Select(_ => ShiftFor(Customer(Addr("Zürich", "ZH")), LinkTo(_zurich)))
            .ToArray();
        var open = ShiftFor(Customer(Addr("Uster", "ZH")), LinkTo(_zurichRegion));

        var plan = ShiftCityGroupPlanner.Plan([.. alreadyPlaced, open], _groups, _centroids, Today, maxCount: 1);

        plan.SkippedAlreadyInCityGroupCount.ShouldBe(3);
        plan.Assignments.Single().ShiftId.ShouldBe(open.Id);
    }

    [Test]
    public void MaxCount_StopsAfterTheGivenNumberOfShifts()
    {
        var plan = ShiftCityGroupPlanner.Plan(
            [ShiftFor(Customer(Addr("Uster", "ZH"))), ShiftFor(Customer(Addr("Zürich", "ZH")))],
            _groups, _centroids, Today, maxCount: 1);

        plan.Assignments.Count.ShouldBe(1);
    }

    [Test]
    public void StateNamedLikeACity_StillNarrowsWhenTheNameIsUnique()
    {
        var plan = Plan(ShiftFor(Customer(Addr("Horw", "LU", 47.02, 8.31))));

        plan.Assignments.Single().MatchReason.ShouldStartWith("nearest city group in the customer's state");
    }

    [Test]
    public void CityGroupsWithoutCoordinatesOrAddresses_AreReportedAsUnlocated()
    {
        var plan = Plan();

        plan.UnlocatedCityGroupNames.ShouldBe(["AG", "Genf Stadt"]);
    }

    [TestCase("Luzern", "Luzern Stadt", true)]
    [TestCase("St. Gallen", "St. Gallen Stadt", true)]
    [TestCase("Weiningen ZH", "Weiningen", true)]
    [TestCase("Biel", "Biel/Bienne", true)]
    [TestCase("  zürich ", "Zürich", true)]
    [TestCase("Bern", "Bernex", false)]
    [TestCase("Genève", "Genf Stadt", false)]
    [TestCase("", "Bern", false)]
    public void PartialNameMatch_RequiresAWordBoundary(string city, string groupName, bool expected)
    {
        ShiftCityGroupPlanner.IsPartialNameMatch(city, groupName).ShouldBe(expected);
    }
}
