// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AnalyzeGroupSemanticsSkill: category detection incl. multi-match, per-group
/// active member counts that ignore scenario and shift-assignment rows, the ungrouped count per
/// requested entity type, the empty-scope fallback, and group-scope restriction.
/// </summary>

using Klacks.Api.Application.DTOs.Groups;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AnalyzeGroupSemanticsSkillTests
{
    private IGroupRepository _groupRepository = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private IClientRepository _clientRepository = null!;
    private IContractRepository _contractRepository = null!;
    private IQualificationRepository _qualificationRepository = null!;
    private IMediator _mediator = null!;

    private readonly Guid _bernGroupId = Guid.NewGuid();
    private readonly Guid _zurichGroupId = Guid.NewGuid();
    private readonly Guid _otherGroupId = Guid.NewGuid();
    private readonly Guid _geoGroupId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _clientRepository = Substitute.For<IClientRepository>();
        _contractRepository = Substitute.For<IContractRepository>();
        _qualificationRepository = Substitute.For<IQualificationRepository>();
        _mediator = Substitute.For<IMediator>();

        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = _bernGroupId, Name = "Bern" },
            new() { Id = _zurichGroupId, Name = "Zürich" },
            new() { Id = _otherGroupId, Name = "Administration" },
            new() { Id = _geoGroupId, Name = "Aussendienst Ost", Latitude = 47.0, Longitude = 8.0 }
        });

        _mediator.Send(Arg.Any<ListQuery<StateResource>>(), Arg.Any<CancellationToken>())
            .Returns(new List<StateResource>
            {
                new() { Abbreviation = "BE", Name = new MultiLanguage { De = "Bern", En = "Bern", Fr = "Berne", It = "Berna" } },
                new() { Abbreviation = "ZH", Name = new MultiLanguage { De = "Zürich", En = "Zurich", Fr = "Zurich", It = "Zurigo" } }
            }.AsEnumerable());

        _clientRepository.GetActiveClientsWithAddressesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Client>
            {
                ClientWithHomeCity("Zürich")
            });

        _qualificationRepository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Qualification>());

        _contractRepository.List().Returns(new List<Contract>());

        _groupItemRepository.GetQuery().Returns(new TestAsyncEnumerable<GroupItem>(new List<GroupItem>()));

        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(Arg.Any<EntityTypeEnum>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client>());
    }

    private AnalyzeGroupSemanticsSkill Skill(IGroupScopeGuard? scopeGuard = null) => new(
        _groupRepository,
        scopeGuard ?? TestGroupScopeGuard.Unrestricted(),
        _groupItemRepository,
        _clientRepository,
        _contractRepository,
        _qualificationRepository,
        _mediator);

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    private static Client ClientWithHomeCity(string city) => new()
    {
        Id = Guid.NewGuid(),
        Type = EntityTypeEnum.Employee,
        Addresses = new List<Address>
        {
            new() { Id = Guid.NewGuid(), Type = AddressTypeEnum.Employee, City = city, ValidFrom = DateTime.UtcNow }
        },
        GroupItems = new List<GroupItem>()
    };

    private static Client ClientWithMembership(bool hasActiveMembership, EntityTypeEnum type = EntityTypeEnum.Employee)
    {
        var groupItems = hasActiveMembership
            ? new List<GroupItem> { new() { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), GroupId = Guid.NewGuid() } }
            : new List<GroupItem>();

        return new Client { Id = Guid.NewGuid(), Type = type, Addresses = new List<Address>(), GroupItems = groupItems };
    }

    [Test]
    public async Task DetectsCategories_IncludingMultiMatchAndGeo_AndFallsBackToOther()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue(result.Message);
        var data = (AnalyzeGroupSemanticsResult)result.Data!;

        data.TotalGroupCount.ShouldBe(4);

        var canton = data.CategorySummaries.Single(s => s.Category == GroupSemanticCategory.Canton);
        canton.Count.ShouldBe(2);
        canton.ExampleGroupNames.ShouldBe(["Bern", "Zürich"], ignoreOrder: true);

        var city = data.CategorySummaries.Single(s => s.Category == GroupSemanticCategory.City);
        city.Count.ShouldBe(1);
        city.ExampleGroupNames.ShouldBe(["Zürich"]);

        var geo = data.CategorySummaries.Single(s => s.Category == GroupSemanticCategory.Geo);
        geo.Count.ShouldBe(1);
        geo.ExampleGroupNames.ShouldBe(["Aussendienst Ost"]);

        var other = data.CategorySummaries.Single(s => s.Category == GroupSemanticCategory.Other);
        other.Count.ShouldBe(1);
        other.ExampleGroupNames.ShouldBe(["Administration"]);

        result.Message.ShouldContain("Canton");
        result.Message.ShouldContain("fill_group_by_criteria");
    }

    [Test]
    public async Task CapsExampleGroupNames_AtFive()
    {
        var manyCantonGroups = Enumerable.Range(1, 7)
            .Select(i => new Group { Id = Guid.NewGuid(), Name = "Bern" })
            .ToList();
        _groupRepository.List().Returns(manyCantonGroups);

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        var data = (AnalyzeGroupSemanticsResult)result.Data!;
        var canton = data.CategorySummaries.Single(s => s.Category == GroupSemanticCategory.Canton);

        canton.Count.ShouldBe(7);
        canton.ExampleGroupNames.Count.ShouldBe(5);
    }

    [Test]
    public async Task CountsActiveMembers_ExcludingScenarioAndShiftAssignments()
    {
        _groupItemRepository.GetQuery().Returns(new TestAsyncEnumerable<GroupItem>(new List<GroupItem>
        {
            new() { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), GroupId = _bernGroupId },
            new() { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), GroupId = _bernGroupId },
            new() { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), GroupId = _bernGroupId, AnalyseToken = Guid.NewGuid() },
            new() { Id = Guid.NewGuid(), ShiftId = Guid.NewGuid(), GroupId = _bernGroupId }
        }));

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        var data = (AnalyzeGroupSemanticsResult)result.Data!;
        var bern = data.GroupMemberCounts.Single(m => m.GroupId == _bernGroupId);

        bern.ActiveMemberCount.ShouldBe(2);
    }

    [Test]
    public async Task ComputesUngroupedCount_ForDefaultEmployeeType()
    {
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>())
            .Returns(new List<Client>
            {
                ClientWithMembership(hasActiveMembership: false),
                ClientWithMembership(hasActiveMembership: false),
                ClientWithMembership(hasActiveMembership: true)
            });

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        var data = (AnalyzeGroupSemanticsResult)result.Data!;
        data.UngroupedEntityType.ShouldBe(EntityTypeEnum.Employee.ToString());
        data.UngroupedCount.ShouldBe(2);
        data.TotalClientCountForType.ShouldBe(3);
        result.Message.ShouldContain("2 of 3 Employee(s)");
    }

    [Test]
    public async Task ComputesUngroupedCount_ForRequestedExternEmpType()
    {
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.ExternEmp, Arg.Any<CancellationToken>())
            .Returns(new List<Client> { ClientWithMembership(hasActiveMembership: false, type: EntityTypeEnum.ExternEmp) });

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["entityType"] = "ExternEmp" });

        var data = (AnalyzeGroupSemanticsResult)result.Data!;
        data.UngroupedEntityType.ShouldBe(EntityTypeEnum.ExternEmp.ToString());
        data.UngroupedCount.ShouldBe(1);
        await _clientRepository.Received(1)
            .GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.ExternEmp, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReportsNoGroups_WhenScopeIsEmpty()
    {
        _groupRepository.List().Returns(new List<Group>());

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue(result.Message);
        var data = (AnalyzeGroupSemanticsResult)result.Data!;
        data.TotalGroupCount.ShouldBe(0);
        data.CategorySummaries.ShouldBeEmpty();
        result.Message.ShouldContain("no groups");
    }

    [Test]
    public async Task DetectsCity_FromNonEmployeeAddressType_AndNonEmployeeClients()
    {
        var usterGroupId = Guid.NewGuid();
        _groupRepository.List().Returns(new List<Group> { new() { Id = usterGroupId, Name = "Uster" } });

        _clientRepository.GetActiveClientsWithAddressesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Client>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Type = EntityTypeEnum.Customer,
                    Addresses = new List<Address>
                    {
                        new() { Id = Guid.NewGuid(), Type = AddressTypeEnum.Workplace, City = "Uster", ValidFrom = DateTime.UtcNow }
                    },
                    GroupItems = new List<GroupItem>()
                }
            });

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        var data = (AnalyzeGroupSemanticsResult)result.Data!;
        data.CategorySummaries.Single(s => s.Category == GroupSemanticCategory.City)
            .ExampleGroupNames.ShouldBe(["Uster"]);
    }

    [Test]
    public async Task RestrictsGroups_ToVisibleScope()
    {
        var visibleRootId = Guid.NewGuid();
        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = visibleRootId, Name = "Bern", Root = null },
            new() { Id = Guid.NewGuid(), Name = "Zürich", Root = Guid.NewGuid() }
        });

        var restrictedGuard = TestGroupScopeGuard.Restricted([visibleRootId], "Bern");
        var result = await Skill(restrictedGuard).ExecuteAsync(Ctx(), new Dictionary<string, object>());

        var data = (AnalyzeGroupSemanticsResult)result.Data!;
        data.TotalGroupCount.ShouldBe(1);
        data.CategorySummaries.Single(s => s.Category == GroupSemanticCategory.Canton).ExampleGroupNames
            .ShouldBe(["Bern"]);
    }
}
