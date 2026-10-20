// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for PartitionClientsByAddressSkill: any unrecognised entityType, level or out-of-range
/// clusterSharePercent is rejected before the command is ever sent, a restricted group scope is
/// refused (this skill creates groups at the top of the tree), an unresolvable rootGroupName is
/// rejected with the real group names, entityType defaults to all three client types and 'Customer'
/// is accepted, and a valid call resolves rootGroupName and forwards apply/level/entityType/
/// clusterSharePercent as given.
/// </summary>

using Klacks.Api.Application.Commands.Groups;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Groups;
using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Services.Geo;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class PartitionClientsByAddressSkillTests
{
    private IGroupRepository _groupRepository = null!;
    private IMediator _mediator = null!;
    private ICompanyClock _companyClock = null!;

    private static readonly Guid HeadOfficeGroupId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _mediator = Substitute.For<IMediator>();
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>())
            .Returns(new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc));

        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = HeadOfficeGroupId, Name = "Hauptsitz" }
        });

        _mediator.Send(Arg.Any<PartitionClientsByAddressCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var cmd = ci.Arg<PartitionClientsByAddressCommand>();
                return Task.FromResult(new PartitionClientsByAddressResult(
                    cmd.Apply, cmd.Level.ToString(), cmd.EntityTypes.Count == 1 ? cmd.EntityTypes[0].ToString() : "All", 1, 0, 0,
                    cmd.Apply ? 1 : 0, cmd.Apply ? 1 : 0, 0,
                    new List<PartitionGroupSummary> { new("BE", null, false, null, 1) },
                    new List<Klacks.Api.Application.DTOs.Grouping.UnassignablePartitionClient>(),
                    new List<string>(),
                    0));
            });
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients", "CanViewGroups" }
    };

    private PartitionClientsByAddressSkill Skill(IGroupScopeGuard? scopeGuard = null) =>
        new(_groupRepository, scopeGuard ?? TestGroupScopeGuard.Unrestricted(), _mediator, _companyClock);

    [Test]
    public async Task ReturnsError_WhenEntityTypeIsUnrecognised()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["entityType"] = "Robot" });

        Assert.That(result.Success, Is.False);
        await _mediator.DidNotReceive().Send(Arg.Any<PartitionClientsByAddressCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReturnsError_WhenLevelIsUnrecognised()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["level"] = "street" });

        Assert.That(result.Success, Is.False);
        await _mediator.DidNotReceive().Send(Arg.Any<PartitionClientsByAddressCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReturnsError_WhenGroupScopeIsRestricted()
    {
        var scopeGuard = TestGroupScopeGuard.Restricted(new[] { Guid.NewGuid() }, "Bern");

        var result = await Skill(scopeGuard).ExecuteAsync(Ctx(), new Dictionary<string, object>());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Bern"));
        await _mediator.DidNotReceive().Send(Arg.Any<PartitionClientsByAddressCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReturnsError_ListingRealGroups_WhenRootGroupNameIsHallucinated()
    {
        var result = await Skill().ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["rootGroupName"] = "Nonexistent" });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Hauptsitz"));
        await _mediator.DidNotReceive().Send(Arg.Any<PartitionClientsByAddressCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Preview_SendsApplyFalse_WithResolvedRootGroupId_AndDefaultLevelAndEntityType()
    {
        var result = await Skill().ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["rootGroupName"] = "Hauptsitz" });

        Assert.That(result.Success, Is.True);
        await _mediator.Received(1).Send(
            Arg.Is<PartitionClientsByAddressCommand>(cmd =>
                !cmd.Apply &&
                cmd.Level == GroupPartitionLevelEnum.Cluster &&
                cmd.EntityTypes.Count == 3 &&
                cmd.RootGroupId == HeadOfficeGroupId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Apply_Message_ReportsNewAndReusedGroupCounts()
    {
        _mediator.Send(Arg.Any<PartitionClientsByAddressCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PartitionClientsByAddressResult(
                true, "State", "Employee", 2, 0, 0, 2, 2, 0,
                new List<PartitionGroupSummary>
                {
                    new("Deutschschweiz Mitte", null, true, HeadOfficeGroupId, 0),
                    new("BE", "Deutschschweiz Mitte", false, Guid.NewGuid(), 2)
                },
                new List<Klacks.Api.Application.DTOs.Grouping.UnassignablePartitionClient>(),
                new List<string>(),
                0)));

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["apply"] = true });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("1 new");
        result.Message.ShouldContain("1 reused");
    }

    [Test]
    public async Task Preview_WarnsHowManyUsersKeepFullVisibility_WhenThisIsTheFirstGroup()
    {
        _mediator.Send(Arg.Any<PartitionClientsByAddressCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PartitionClientsByAddressResult(
                false, "State", "Employee", 2, 0, 0, 0, 0, 0,
                new List<PartitionGroupSummary> { new("BE", null, false, null, 2) },
                new List<Klacks.Api.Application.DTOs.Grouping.UnassignablePartitionClient>(),
                new List<string>(),
                4)));

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("first group");
        result.Message.ShouldContain("4 user(s) keep access to everything");
    }

    [Test]
    public async Task Preview_SaysNothingAboutVisibility_WhenGroupsAlreadyExist()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldNotContain("keep access to everything");
    }

    [Test]
    public async Task Apply_ForwardsApplyTrue_AndIncludeAlreadyGrouped()
    {
        var parameters = new Dictionary<string, object>
        {
            ["apply"] = true,
            ["includeAlreadyGrouped"] = true,
            ["level"] = "city"
        };

        await Skill().ExecuteAsync(Ctx(), parameters);

        await _mediator.Received(1).Send(
            Arg.Is<PartitionClientsByAddressCommand>(cmd =>
                cmd.Apply && cmd.IncludeAlreadyGrouped && cmd.Level == GroupPartitionLevelEnum.City),
            Arg.Any<CancellationToken>());
    }

    [TestCase("state", GroupPartitionLevelEnum.State)]
    [TestCase("state_city", GroupPartitionLevelEnum.StateCity)]
    [TestCase("cluster", GroupPartitionLevelEnum.Cluster)]
    [TestCase("cluster_municipality", GroupPartitionLevelEnum.ClusterMunicipality)]
    public async Task Execute_LevelNames_MapToLevels(string level, GroupPartitionLevelEnum expected)
    {
        var parameters = new Dictionary<string, object> { ["level"] = level };

        var result = await Skill().ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<PartitionClientsByAddressCommand>(c => c.Level == expected), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Execute_UnknownLevel_ReturnsErrorNamingNeutralLevels()
    {
        var parameters = new Dictionary<string, object> { ["level"] = "kanton" };

        var result = await Skill().ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("cluster, cluster_municipality, state, city, state_city");
    }

    [Test]
    public async Task Execute_NoParameters_DefaultsToClusterLevelAndAllTypesWithTenPercent()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<PartitionClientsByAddressCommand>(c =>
                c.Level == GroupPartitionLevelEnum.Cluster &&
                c.EntityTypes.Count == 3 &&
                c.ClusterSharePercent == 10 &&
                !c.Apply),
            Arg.Any<CancellationToken>());
    }

    [TestCase("Employee", EntityTypeEnum.Employee)]
    [TestCase("ExternEmp", EntityTypeEnum.ExternEmp)]
    [TestCase("customer", EntityTypeEnum.Customer)]
    public async Task Execute_SingleEntityType_IsForwarded(string entityType, EntityTypeEnum expected)
    {
        var parameters = new Dictionary<string, object> { ["entityType"] = entityType };

        var result = await Skill().ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<PartitionClientsByAddressCommand>(c => c.EntityTypes.Count == 1 && c.EntityTypes[0] == expected),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Execute_UnknownEntityType_ListsAllFour()
    {
        var parameters = new Dictionary<string, object> { ["entityType"] = "Kunde" };

        var result = await Skill().ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("All, Employee, ExternEmp, Customer");
    }

    [TestCase(0)]
    [TestCase(101)]
    public async Task Execute_ClusterShareOutOfRange_ReturnsError(int share)
    {
        var parameters = new Dictionary<string, object> { ["clusterSharePercent"] = share };

        var result = await Skill().ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("clusterSharePercent");
        await _mediator.DidNotReceive().Send(Arg.Any<PartitionClientsByAddressCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Execute_ClusterShareProvided_IsPassedThrough()
    {
        var parameters = new Dictionary<string, object> { ["clusterSharePercent"] = 25 };

        await Skill().ExecuteAsync(Ctx(), parameters);

        await _mediator.Received(1).Send(
            Arg.Is<PartitionClientsByAddressCommand>(c => c.ClusterSharePercent == 25), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SubClusterSharePercent_DefaultsTo15_AndIsForwarded()
    {
        await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["level"] = "cluster_municipality" });
        await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["level"] = "cluster_municipality", ["subClusterSharePercent"] = 25 });

        await _mediator.Received(1).Send(
            Arg.Is<PartitionClientsByAddressCommand>(cmd => cmd.SubClusterSharePercent == 15), Arg.Any<CancellationToken>());
        await _mediator.Received(1).Send(
            Arg.Is<PartitionClientsByAddressCommand>(cmd => cmd.SubClusterSharePercent == 25), Arg.Any<CancellationToken>());
    }

    [TestCase(0)]
    [TestCase(101)]
    public async Task ReturnsError_WhenSubClusterSharePercentIsOutOfRange(int share)
    {
        var result = await Skill().ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["level"] = "cluster_municipality", ["subClusterSharePercent"] = share });

        result.Success.ShouldBeFalse();
        await _mediator.DidNotReceive().Send(Arg.Any<PartitionClientsByAddressCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Preview_MunicipalityLevel_ShowsFourLevelTreeAndWhereSmallPlacesWent()
    {
        _mediator.Send(Arg.Any<PartitionClientsByAddressCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PartitionClientsByAddressResult(
                false, nameof(GroupPartitionLevelEnum.ClusterMunicipality), "All", 29, 0, 0, 0, 0, 0,
                new List<PartitionGroupSummary>
                {
                    new("Deutschschweiz Zürich", null, false, null, 0),
                    new("ZH", "Deutschschweiz Zürich", false, null, 0),
                    new("Winterthur", "ZH", false, null, 14),
                    new("Seuzach", "Winterthur", false, null, 7)
                },
                new List<Klacks.Api.Application.DTOs.Grouping.UnassignablePartitionClient>(),
                new List<string>(),
                0,
                new List<Klacks.Api.Application.DTOs.Grouping.PartitionPlaceAttachment>
                {
                    new("Hettlingen", "Winterthur", "Seuzach", true, 2, 2.2),
                    new("Wülflingen", "Winterthur", "Winterthur", false, 2, null)
                })));

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["level"] = "cluster_municipality" });

        result.Message.ShouldContain("region → state → city cluster");
        result.Message.ShouldContain("municipality sub-cluster");
        result.Message.ShouldContain("Seuzach (7, new, in Winterthur)");
        result.Message.ShouldContain("Hettlingen (2) joins the nearer sub-cluster Seuzach");
        result.Message.ShouldContain("Wülflingen (2) stays directly in Winterthur");
        result.Message.ShouldContain("Nothing was changed yet");
    }

    private const string ClusterMethod =
        "Method: cluster centres are the cities holding at least 10% of their state's addresses " +
        "(the largest city of a state always is one; thresholds: ZH 183 of 1829); clients of a centre city join it by city name";

    private static PartitionClientsByAddressResult ClusterResult(bool applied, int skipped = 0) =>
        new(
            applied, nameof(GroupPartitionLevelEnum.Cluster), "All", 1830, skipped, 3, applied ? 1829 : 0, applied ? 1829 : 0, 0,
            new List<PartitionGroupSummary>
            {
                new("Deutschschweiz Zürich", null, true, HeadOfficeGroupId, 0, Method: PartitionGroupMethods.Region),
                new("ZH", "Deutschschweiz Zürich", true, Guid.NewGuid(), 0, Method: PartitionGroupMethods.State),
                new("Zürich", "ZH", false, null, 1829, HasCoordinates: true, DirectCount: 1495, AttachedCount: 334,
                    AttachedPlaceCount: 5, Method: PartitionGroupMethods.Cluster)
            },
            new List<UnassignablePartitionClient>
            {
                new(Guid.NewGuid(), "Anna Meier", "no address on record"),
                new(Guid.NewGuid(), "Beat Huber", "no address on record"),
                new(Guid.NewGuid(), "Cora Graf", "place 'Hinterdorf' has no geocoded address, so its distance to the cluster centres cannot be measured")
            },
            new List<string>(),
            0,
            PopulationCount: 5000,
            UnassignableByReason: new List<PartitionReasonCount>
            {
                new("no address on record", 2),
                new("place 'Hinterdorf' has no geocoded address, so its distance to the cluster centres cannot be measured", 1)
            },
            ClusterPlaceAttachments: new List<ClusterPlaceAttachment>
            {
                new("CH", "ZH", "Oberengstringen", "Zürich", 85, 80, 6.34),
                new("CH", "ZH", "Hinterdorf", "Zürich", 2, 0, null)
            },
            Method: ClusterMethod + "; every other place joins the nearest centre of its state as a whole.");

    [Test]
    public async Task Preview_ClusterLevel_StatesTheMethod_ThePerGroupBasis_AndWherePlacesWent()
    {
        _mediator.Send(Arg.Any<PartitionClientsByAddressCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ClusterResult(applied: false)));

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Message.ShouldContain(ClusterMethod);
        result.Message.ShouldContain("Zürich (1829: 1495 by city name + 334 attached from 5 place(s); centre has coordinates, new)");
        result.Message.ShouldContain("Oberengstringen (85) → Zürich, 6.3 km");
        result.Message.ShouldContain("Hinterdorf (2) → Zürich, only centre of its state (distance not measured)");
        result.Message.ShouldContain("ZH (0, existing)");
    }

    [Test]
    public async Task Preview_GroupsTheUnassignableClientsByReasonWithCounts()
    {
        _mediator.Send(Arg.Any<PartitionClientsByAddressCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ClusterResult(applied: false)));

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Message.ShouldContain(
            "3 could not be placed (2× no address on record, 1× place 'Hinterdorf' has no geocoded address, so its distance to the cluster centres cannot be measured)");
    }

    [Test]
    public async Task Preview_WithSkippedClients_SaysTheStructureCameFromEveryClient()
    {
        _mediator.Send(Arg.Any<PartitionClientsByAddressCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ClusterResult(applied: false, skipped: 4615)));

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Message.ShouldContain(
            "structure computed from all 5000 client(s) with a usable address; 4615 already grouped kept their membership and were not re-placed");
    }

    [Test]
    public async Task Apply_ClusterLevel_RepeatsTheMethodAndThePerGroupBasis()
    {
        _mediator.Send(Arg.Any<PartitionClientsByAddressCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ClusterResult(applied: true)));

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["apply"] = true });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("Added 1829 membership(s)");
        result.Message.ShouldContain(ClusterMethod);
        result.Message.ShouldContain("Zürich (1829: 1495 by city name + 334 attached from 5 place(s); centre has coordinates, new)");
        result.Message.ShouldContain("Oberengstringen (85) → Zürich, 6.3 km");
    }
}
