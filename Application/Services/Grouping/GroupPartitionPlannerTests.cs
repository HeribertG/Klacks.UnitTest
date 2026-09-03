// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GroupPartitionPlanner: a pure function, so every case here builds clients and
/// existing groups in memory and asserts on the returned plan without any repository or database.
/// Covers all three levels (canton, city, canton_city), the region-root mirroring of GroupsSeed versus
/// a caller-supplied root, reuse of an already-existing group by (name, parent), the already-grouped
/// skip (and that a scenario membership does not count as "already grouped"), clients that cannot be
/// placed because their address is missing the field(s) the level needs, and the duplicate-name warning
/// for a planned group whose name already exists elsewhere in the tree.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Services.Grouping;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class GroupPartitionPlannerTests
{
    [Test]
    public void Plan_CantonLevel_GroupsClientsByCanton_UnderRegionRoot()
    {
        var bern = ClientWithAddress("Anna", "Meier", "BE", "Bern");
        var zurich = ClientWithAddress("Beat", "Huber", "ZH", "Zürich");

        var plan = GroupPartitionPlanner.Plan(
            new[] { bern, zurich }, existingGroups: [], GroupPartitionLevelEnum.Canton,
            rootGroupId: null, includeAlreadyGrouped: false);

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
    public void Plan_CityLevel_IsFlat_NoCantonOrRegionNodes()
    {
        var bern = ClientWithAddress("Anna", "Meier", "BE", "Bern");
        var zurich = ClientWithAddress("Beat", "Huber", "ZH", "Zürich");

        var plan = GroupPartitionPlanner.Plan(
            new[] { bern, zurich }, existingGroups: [], GroupPartitionLevelEnum.City,
            rootGroupId: null, includeAlreadyGrouped: false);

        plan.Groups.Count.ShouldBe(2);
        plan.Groups.ShouldAllBe(g => g.ParentKey == null);
        var bernNode = plan.Groups.Single(g => g.Name == "Bern");
        bernNode.ClientCount.ShouldBe(1);
        plan.Assignments.ShouldContain(a => a.ClientId == bern.Id && a.LeafGroupKey == bernNode.Key);
    }

    [Test]
    public void Plan_CantonCityLevel_NestsCityUnderCantonUnderRegion()
    {
        var bern1 = ClientWithAddress("Anna", "Meier", "BE", "Bern");
        var bern2 = ClientWithAddress("Rolf", "Wenger", "BE", "Bern");
        var zurich = ClientWithAddress("Beat", "Huber", "ZH", "Zürich");

        var plan = GroupPartitionPlanner.Plan(
            new[] { bern1, bern2, zurich }, existingGroups: [], GroupPartitionLevelEnum.CantonCity,
            rootGroupId: null, includeAlreadyGrouped: false);

        plan.Groups.Count.ShouldBe(6); // 2 regions + 2 cantons + 2 cities
        var beCanton = plan.Groups.Single(g => g.Name == "BE");
        var bernCity = plan.Groups.Single(g => g.Name == "Bern");
        beCanton.ClientCount.ShouldBe(0);
        bernCity.ClientCount.ShouldBe(2);
        bernCity.ParentKey.ShouldBe(beCanton.Key);
        plan.Assignments.Count(a => a.LeafGroupKey == bernCity.Key).ShouldBe(2);
    }

    [Test]
    public void Plan_RootGroupIdGiven_SkipsRegionLayer_CantonsAttachDirectlyUnderRoot()
    {
        var rootId = Guid.NewGuid();
        var bern = ClientWithAddress("Anna", "Meier", "BE", "Bern");

        var plan = GroupPartitionPlanner.Plan(
            new[] { bern }, existingGroups: [], GroupPartitionLevelEnum.CantonCity,
            rootGroupId: rootId, includeAlreadyGrouped: false);

        plan.Groups.ShouldNotContain(g => g.Name.StartsWith("Deutschschweiz") || g.Name == "Westschweiz");
        var beCanton = plan.Groups.Single(g => g.Name == "BE");
        beCanton.ParentKey.ShouldBeNull();
    }

    [Test]
    public void Plan_ClientWithoutCity_IsUnassignable_AtCantonCityLevel()
    {
        var noCity = ClientWithAddress("Anna", "Meier", "BE", city: "");

        var plan = GroupPartitionPlanner.Plan(
            new[] { noCity }, existingGroups: [], GroupPartitionLevelEnum.CantonCity,
            rootGroupId: null, includeAlreadyGrouped: false);

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

        var plan = GroupPartitionPlanner.Plan(
            new[] { noAddress }, existingGroups: [], GroupPartitionLevelEnum.Canton,
            rootGroupId: null, includeAlreadyGrouped: false);

        plan.Unassignable.ShouldHaveSingleItem();
        plan.Unassignable[0].Reason.ShouldBe("no address on record");
    }

    [Test]
    public void Plan_AlreadyGroupedClient_IsSkipped_ByDefault()
    {
        var client = ClientWithAddress("Anna", "Meier", "BE", "Bern");
        client.GroupItems.Add(new GroupItem { Id = Guid.NewGuid(), ClientId = client.Id, GroupId = Guid.NewGuid() });

        var plan = GroupPartitionPlanner.Plan(
            new[] { client }, existingGroups: [], GroupPartitionLevelEnum.Canton,
            rootGroupId: null, includeAlreadyGrouped: false);

        plan.SkippedAlreadyGroupedCount.ShouldBe(1);
        plan.Assignments.ShouldBeEmpty();
        plan.Groups.ShouldBeEmpty();
    }

    [Test]
    public void Plan_AlreadyGroupedClient_IsIncluded_WhenIncludeAlreadyGroupedIsTrue()
    {
        var client = ClientWithAddress("Anna", "Meier", "BE", "Bern");
        client.GroupItems.Add(new GroupItem { Id = Guid.NewGuid(), ClientId = client.Id, GroupId = Guid.NewGuid() });

        var plan = GroupPartitionPlanner.Plan(
            new[] { client }, existingGroups: [], GroupPartitionLevelEnum.Canton,
            rootGroupId: null, includeAlreadyGrouped: true);

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

        var plan = GroupPartitionPlanner.Plan(
            new[] { client }, existingGroups: [], GroupPartitionLevelEnum.Canton,
            rootGroupId: null, includeAlreadyGrouped: false);

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

        var plan = GroupPartitionPlanner.Plan(
            new[] { bern }, new[] { region, existingBe }, GroupPartitionLevelEnum.Canton,
            rootGroupId: null, includeAlreadyGrouped: false);

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

        var plan = GroupPartitionPlanner.Plan(
            new[] { bern }, new[] { unrelatedBe }, GroupPartitionLevelEnum.Canton,
            rootGroupId: null, includeAlreadyGrouped: false);

        var beNode = plan.Groups.Single(g => g.Name == "BE");
        beNode.Existed.ShouldBeFalse();
        plan.Warnings.ShouldContain(w => w.Contains("'BE'") && w.Contains(unrelatedBe.Id.ToString()));
    }

    [Test]
    public void Plan_CantonCity_TreatsDifferentCasingOfTheSameCity_AsOneGroup()
    {
        var lower = ClientWithAddress("Anna", "Meier", "BE", "Bern");
        var upper = ClientWithAddress("Rolf", "Wenger", "BE", "BERN");

        var plan = GroupPartitionPlanner.Plan(
            new[] { lower, upper }, existingGroups: [], GroupPartitionLevelEnum.CantonCity,
            rootGroupId: null, includeAlreadyGrouped: false);

        plan.Groups.Count(g => string.Equals(g.Name, "Bern", StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
        var cityNode = plan.Groups.Single(g => string.Equals(g.Name, "Bern", StringComparison.OrdinalIgnoreCase));
        cityNode.ClientCount.ShouldBe(2);
    }

    private static Client ClientWithAddress(string firstName, string lastName, string canton, string city)
    {
        var clientId = Guid.NewGuid();
        return new Client
        {
            Id = clientId,
            FirstName = firstName,
            Name = lastName,
            Type = EntityTypeEnum.Employee,
            Addresses = new List<Address>
            {
                new()
                {
                    ClientId = clientId,
                    Type = AddressTypeEnum.Employee,
                    State = canton,
                    City = city,
                    ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                }
            }
        };
    }
}
