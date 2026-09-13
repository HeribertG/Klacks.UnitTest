// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GroupPartitionPlanner: a pure function, so every case here builds clients and
/// existing groups in memory and asserts on the returned plan without any repository or database.
/// Covers all four levels (State, City, StateCity, Cluster), the region-root mirroring of a supplied
/// region map versus a caller-supplied root, reuse of an already-existing group by (name, parent), the
/// already-grouped skip (and that a scenario membership does not count as "already grouped"), clients
/// that cannot be placed because their address is missing the field(s) the level needs, and the
/// duplicate-name warning for a planned group whose name already exists elsewhere in the tree.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Geo;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class GroupPartitionPlannerTests
{
    private const string Switzerland = "CH";
    private const int DefaultSharePercent = 10;

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> SwissRegions =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            [Switzerland] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["BE"] = "Deutschschweiz Mitte",
                ["ZH"] = "Deutschschweiz Zürich",
                ["AG"] = "Deutschschweiz Zürich",
                ["GE"] = "Westschweiz"
            }
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> NoRegions =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> StateNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["CH|BE"] = "Bern", ["CH|ZH"] = "Zürich" };

    private static GroupPartitionPlan PlanWith(
        IReadOnlyList<Client> clients,
        GroupPartitionLevelEnum level,
        IReadOnlyList<Group>? existingGroups = null,
        Guid? rootGroupId = null,
        bool includeAlreadyGrouped = false,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? regions = null,
        int sharePercent = DefaultSharePercent) =>
        GroupPartitionPlanner.Plan(
            clients, existingGroups ?? [], level, rootGroupId, includeAlreadyGrouped,
            new GroupPartitionContext(Switzerland, regions ?? SwissRegions, StateNames, sharePercent));

    [Test]
    public void Plan_StateLevel_GroupsClientsByState_UnderRegionRoot()
    {
        var bern = ClientWithAddress("Anna", "Meier", "BE", "Bern");
        var zurich = ClientWithAddress("Beat", "Huber", "ZH", "Zürich");

        var plan = PlanWith([bern, zurich], GroupPartitionLevelEnum.State);

        plan.Groups.ShouldContain(g => g.Name == "Deutschschweiz Mitte" && g.ParentKey == null && g.ClientCount == 0);
        plan.Groups.ShouldContain(g => g.Name == "Deutschschweiz Zürich" && g.ParentKey == null && g.ClientCount == 0);

        var beNode = plan.Groups.Single(g => g.Name == "BE");
        var zhNode = plan.Groups.Single(g => g.Name == "ZH");
        beNode.ClientCount.ShouldBe(1);
        zhNode.ClientCount.ShouldBe(1);
        beNode.ParentKey.ShouldBe(plan.Groups.Single(g => g.Name == "Deutschschweiz Mitte").Key);
        zhNode.ParentKey.ShouldBe(plan.Groups.Single(g => g.Name == "Deutschschweiz Zürich").Key);

        plan.Assignments.ShouldContain(a => a.ClientId == bern.Id && a.LeafGroupKey == beNode.Key);
        plan.Assignments.ShouldContain(a => a.ClientId == zurich.Id && a.LeafGroupKey == zhNode.Key);
        plan.Groups.Count(g => g.Name is "BE" or "ZH").ShouldBe(2);
        plan.Groups.Count.ShouldBe(4);
    }

    [Test]
    public void Plan_CityLevel_IsFlat_NoStateOrRegionNodes()
    {
        var bern = ClientWithAddress("Anna", "Meier", "BE", "Bern");
        var zurich = ClientWithAddress("Beat", "Huber", "ZH", "Zürich");

        var plan = PlanWith([bern, zurich], GroupPartitionLevelEnum.City);

        plan.Groups.Count.ShouldBe(2);
        plan.Groups.ShouldAllBe(g => g.ParentKey == null);
        var bernNode = plan.Groups.Single(g => g.Name == "Bern");
        bernNode.ClientCount.ShouldBe(1);
        plan.Assignments.ShouldContain(a => a.ClientId == bern.Id && a.LeafGroupKey == bernNode.Key);
    }

    [Test]
    public void Plan_StateCityLevel_NestsCityUnderStateUnderRegion()
    {
        var bern1 = ClientWithAddress("Anna", "Meier", "BE", "Bern");
        var bern2 = ClientWithAddress("Rolf", "Wenger", "BE", "Bern");
        var zurich = ClientWithAddress("Beat", "Huber", "ZH", "Zürich");

        var plan = PlanWith([bern1, bern2, zurich], GroupPartitionLevelEnum.StateCity);

        plan.Groups.Count.ShouldBe(6); // 2 regions + 2 states + 2 cities
        var beState = plan.Groups.Single(g => g.Name == "BE");
        var bernCity = plan.Groups.Single(g => g.Name == "Bern");
        beState.ClientCount.ShouldBe(0);
        bernCity.ClientCount.ShouldBe(2);
        bernCity.ParentKey.ShouldBe(beState.Key);
        plan.Assignments.Count(a => a.LeafGroupKey == bernCity.Key).ShouldBe(2);
    }

    [Test]
    public void Plan_RootGroupIdGiven_SkipsRegionLayer_StatesAttachDirectlyUnderRoot()
    {
        var rootId = Guid.NewGuid();
        var bern = ClientWithAddress("Anna", "Meier", "BE", "Bern");

        var plan = PlanWith([bern], GroupPartitionLevelEnum.StateCity, rootGroupId: rootId);

        plan.Groups.ShouldNotContain(g => g.Name.StartsWith("Deutschschweiz") || g.Name == "Westschweiz");
        var beState = plan.Groups.Single(g => g.Name == "BE");
        beState.ParentKey.ShouldBeNull();
    }

    [Test]
    public void Plan_ClientWithoutCity_IsUnassignable_AtStateCityLevel()
    {
        var noCity = ClientWithAddress("Anna", "Meier", "BE", city: "");

        var plan = PlanWith([noCity], GroupPartitionLevelEnum.StateCity);

        plan.Unassignable.ShouldHaveSingleItem();
        plan.Unassignable[0].ClientId.ShouldBe(noCity.Id);
        plan.Unassignable[0].Reason.ShouldBe("address has no city");
        plan.Assignments.ShouldBeEmpty();
        plan.Groups.ShouldBeEmpty();
    }

    [Test]
    public void Plan_ClientWithNoAddressAtAll_IsUnassignable()
    {
        var noAddress = new Client { Id = Guid.NewGuid(), FirstName = "No", Name = "Address" };

        var plan = PlanWith([noAddress], GroupPartitionLevelEnum.State);

        plan.Unassignable.ShouldHaveSingleItem();
        plan.Unassignable[0].Reason.ShouldBe("no address on record");
    }

    [Test]
    public void Plan_AlreadyGroupedClient_IsSkipped_ByDefault()
    {
        var client = ClientWithAddress("Anna", "Meier", "BE", "Bern");
        client.GroupItems.Add(new GroupItem { Id = Guid.NewGuid(), ClientId = client.Id, GroupId = Guid.NewGuid() });

        var plan = PlanWith([client], GroupPartitionLevelEnum.State);

        plan.SkippedAlreadyGroupedCount.ShouldBe(1);
        plan.Assignments.ShouldBeEmpty();
        plan.Groups.ShouldBeEmpty();
    }

    [Test]
    public void Plan_AlreadyGroupedClient_IsIncluded_WhenIncludeAlreadyGroupedIsTrue()
    {
        var client = ClientWithAddress("Anna", "Meier", "BE", "Bern");
        client.GroupItems.Add(new GroupItem { Id = Guid.NewGuid(), ClientId = client.Id, GroupId = Guid.NewGuid() });

        var plan = PlanWith([client], GroupPartitionLevelEnum.State, includeAlreadyGrouped: true);

        plan.SkippedAlreadyGroupedCount.ShouldBe(0);
        plan.Assignments.ShouldHaveSingleItem();
    }

    [Test]
    public void Plan_ScenarioMembership_DoesNotCountAsAlreadyGrouped()
    {
        var client = ClientWithAddress("Anna", "Meier", "BE", "Bern");
        client.GroupItems.Add(new GroupItem
        {
            Id = Guid.NewGuid(), ClientId = client.Id, GroupId = Guid.NewGuid(), AnalyseToken = Guid.NewGuid()
        });

        var plan = PlanWith([client], GroupPartitionLevelEnum.State);

        plan.SkippedAlreadyGroupedCount.ShouldBe(0);
        plan.Assignments.ShouldHaveSingleItem();
    }

    [Test]
    public void Plan_ReusesExistingGroup_ByNameUnderSameParent()
    {
        var regionId = Guid.NewGuid();
        var existingBe = new Group { Id = Guid.NewGuid(), Name = "BE", Parent = regionId };
        var region = new Group { Id = regionId, Name = "Deutschschweiz Mitte", Parent = null };
        var bern = ClientWithAddress("Anna", "Meier", "BE", "Bern");

        var plan = PlanWith([bern], GroupPartitionLevelEnum.State, existingGroups: [region, existingBe]);

        var beNode = plan.Groups.Single(g => g.Name == "BE");
        beNode.Existed.ShouldBeTrue();
        beNode.ExistingGroupId.ShouldBe(existingBe.Id);

        var regionNode = plan.Groups.Single(g => g.Name == "Deutschschweiz Mitte");
        regionNode.Existed.ShouldBeTrue();
        regionNode.ExistingGroupId.ShouldBe(regionId);
    }

    [Test]
    public void Plan_DoesNotReuse_WhenSameNameGroupExistsUnderADifferentParent_AndWarnsInstead()
    {
        var unrelatedParentId = Guid.NewGuid();
        var unrelatedBe = new Group { Id = Guid.NewGuid(), Name = "BE", Parent = unrelatedParentId };
        var bern = ClientWithAddress("Anna", "Meier", "BE", "Bern");

        var plan = PlanWith([bern], GroupPartitionLevelEnum.State, existingGroups: [unrelatedBe]);

        var beNode = plan.Groups.Single(g => g.Name == "BE");
        beNode.Existed.ShouldBeFalse();
        plan.Warnings.ShouldContain(w => w.Contains("'BE'") && w.Contains(unrelatedBe.Id.ToString()));
    }

    [Test]
    public void Plan_StateCity_TreatsDifferentCasingOfTheSameCity_AsOneGroup()
    {
        var lower = ClientWithAddress("Anna", "Meier", "BE", "Bern");
        var upper = ClientWithAddress("Rolf", "Wenger", "BE", "BERN");

        var plan = PlanWith([lower, upper], GroupPartitionLevelEnum.StateCity);

        plan.Groups.Count(g => string.Equals(g.Name, "Bern", StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
        var cityNode = plan.Groups.Single(g => string.Equals(g.Name, "Bern", StringComparison.OrdinalIgnoreCase));
        cityNode.ClientCount.ShouldBe(2);
    }

    [Test]
    public void Plan_StateLevel_WithoutRegionMapping_AttachesStatesToRoot()
    {
        var bern = ClientWithAddress("Anna", "Meier", "BE", "Bern");

        var plan = PlanWith([bern], GroupPartitionLevelEnum.State, regions: NoRegions);

        plan.Groups.Count.ShouldBe(1);
        plan.Groups[0].Name.ShouldBe("BE");
        plan.Groups[0].ParentKey.ShouldBeNull();
        plan.Groups[0].Description.ShouldBe("Bern");
    }

    [Test]
    public void Plan_StateNode_UsesStateDisplayNameAsDescription_FallsBackToCode()
    {
        var bern = ClientWithAddress("Anna", "Meier", "BE", "Bern");
        var glarus = ClientWithAddress("Gian", "Zweifel", "GL", "Glarus");

        var plan = PlanWith([bern, glarus], GroupPartitionLevelEnum.State, regions: NoRegions);

        plan.Groups.Single(g => g.Name == "BE").Description.ShouldBe("Bern");
        plan.Groups.Single(g => g.Name == "GL").Description.ShouldBe("GL");
    }

    [Test]
    public void Plan_ClusterLevel_NestsClusterCitiesUnderStateUnderRegion_WithCentroid()
    {
        var clients = Enumerable.Range(0, 9).Select(i => ClientWithAddress("Z" + i, "Zürich", "ZH", "Zürich", 47.37, 8.54))
            .Append(ClientWithAddress("U", "Uster", "ZH", "Uster", 47.35, 8.72))
            .ToList();

        var plan = PlanWith(clients, GroupPartitionLevelEnum.Cluster);

        var region = plan.Groups.Single(g => g.Name == "Deutschschweiz Zürich");
        var state = plan.Groups.Single(g => g.Name == "ZH");
        var zurich = plan.Groups.Single(g => g.Name == "Zürich");
        var uster = plan.Groups.Single(g => g.Name == "Uster");
        state.ParentKey.ShouldBe(region.Key);
        zurich.ParentKey.ShouldBe(state.Key);
        uster.ParentKey.ShouldBe(state.Key);
        zurich.ClientCount.ShouldBe(9);
        uster.ClientCount.ShouldBe(1);
        zurich.Latitude!.Value.ShouldBe(47.37, 0.0001);
        zurich.Longitude!.Value.ShouldBe(8.54, 0.0001);
        state.Latitude.ShouldBeNull();
        plan.Assignments.Count.ShouldBe(10);
        plan.Assignments.ShouldAllBe(a => a.LeafGroupKey == zurich.Key || a.LeafGroupKey == uster.Key);
    }

    [Test]
    public void Plan_ClusterLevel_SmallTownJoinsNearestCenter_AndCountsThere()
    {
        var clients = Enumerable.Range(0, 20).Select(i => ClientWithAddress("Z" + i, "Zürich", "ZH", "Zürich", 47.37, 8.54))
            .Concat(Enumerable.Range(0, 20).Select(i => ClientWithAddress("W" + i, "Winterthur", "ZH", "Winterthur", 47.50, 8.72)))
            .Append(ClientWithAddress("U", "Uster", "ZH", "Uster", 47.35, 8.72))
            .ToList();
        var uster = clients[^1];

        var plan = PlanWith(clients, GroupPartitionLevelEnum.Cluster);

        plan.Groups.ShouldNotContain(g => g.Name == "Uster");
        var zurich = plan.Groups.Single(g => g.Name == "Zürich");
        zurich.ClientCount.ShouldBe(21);
        plan.Assignments.Single(a => a.ClientId == uster.Id).LeafGroupKey.ShouldBe(zurich.Key);
    }

    [Test]
    public void Plan_ClusterLevel_SmallTownWithoutCoordinates_IsUnassignableWithReason()
    {
        var clients = Enumerable.Range(0, 20).Select(i => ClientWithAddress("Z" + i, "Zürich", "ZH", "Zürich", 47.37, 8.54))
            .Append(ClientWithAddress("U", "Uster", "ZH", "Uster"))
            .ToList();
        var uster = clients[^1];

        var plan = PlanWith(clients, GroupPartitionLevelEnum.Cluster);

        plan.Unassignable.Single().ClientId.ShouldBe(uster.Id);
        plan.Unassignable.Single().Reason.ShouldBe(AddressClusterPlanner.ReasonNoCoordinates);
    }

    [Test]
    public void Plan_ClusterLevel_ExistingClusterGroupUnderState_IsReused()
    {
        var regionId = Guid.NewGuid();
        var stateId = Guid.NewGuid();
        var clusterId = Guid.NewGuid();
        var existing = new List<Group>
        {
            new() { Id = regionId, Name = "Deutschschweiz Zürich" },
            new() { Id = stateId, Name = "ZH", Parent = regionId },
            new() { Id = clusterId, Name = "Zürich", Parent = stateId }
        };
        var clients = new[] { ClientWithAddress("A", "B", "ZH", "Zürich", 47.37, 8.54) };

        var plan = PlanWith(clients, GroupPartitionLevelEnum.Cluster, existingGroups: existing);

        plan.Groups.ShouldAllBe(g => g.Existed);
        plan.Groups.Single(g => g.Name == "Zürich").ExistingGroupId.ShouldBe(clusterId);
    }

    [Test]
    public void Plan_AddressWithoutCountry_UsesDefaultCountryForRegionLookup()
    {
        var bern = ClientWithAddress("Anna", "Meier", "BE", "Bern", country: "");

        var plan = PlanWith([bern], GroupPartitionLevelEnum.State);

        plan.Groups.ShouldContain(g => g.Name == "Deutschschweiz Mitte");
    }

    [Test]
    public void Plan_CustomersAreClientsToo()
    {
        var customer = ClientWithAddress("", "Muster AG", "BE", "Bern", type: EntityTypeEnum.Customer);

        var plan = PlanWith([customer], GroupPartitionLevelEnum.State, regions: NoRegions);

        plan.Assignments.Single().ClientId.ShouldBe(customer.Id);
    }

    [Test]
    public void Plan_CustomerWithCompany_UsesCompanyAsDisplayName()
    {
        var customer = ClientWithAddress("", "x", "BE", "Bern", type: EntityTypeEnum.Customer);
        customer.Company = "Muster AG";

        var plan = PlanWith([customer], GroupPartitionLevelEnum.State, regions: NoRegions);

        plan.Assignments.Single().ClientName.ShouldBe("Muster AG");
    }

    [Test]
    public void Plan_StateLevel_ReasonWordingIsCountryNeutral()
    {
        var noState = ClientWithAddress("Anna", "Meier", "", "Bern");

        var plan = PlanWith([noState], GroupPartitionLevelEnum.State, regions: NoRegions);

        plan.Unassignable.Single().Reason.ShouldBe("address has no state/province");
    }

    private static Client ClientWithAddress(
        string firstName, string lastName, string state, string city,
        double? latitude = null, double? longitude = null, EntityTypeEnum type = EntityTypeEnum.Employee, string country = "CH")
    {
        var clientId = Guid.NewGuid();
        return new Client
        {
            Id = clientId,
            FirstName = firstName,
            Name = lastName,
            Type = type,
            Addresses = new List<Address>
            {
                new()
                {
                    ClientId = clientId,
                    Type = AddressTypeEnum.Employee,
                    State = state,
                    City = city,
                    Country = country,
                    Latitude = latitude,
                    Longitude = longitude,
                    ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                }
            }
        };
    }
}
