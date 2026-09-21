// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for PartitionClientsByAddressSkill: any unrecognised entityType, level or out-of-range
/// clusterSharePercent is rejected before the command is ever sent, a restricted group scope is
/// refused (this skill creates groups at the top of the tree), an unresolvable rootGroupName is
/// rejected with the real group names, entityType defaults to all three client types and 'Customer'
/// is accepted, and a valid call resolves rootGroupName and forwards apply/level/entityType/
/// clusterSharePercent as given.
/// </summary>

using Klacks.Api.Application.Commands.Groups;
using Klacks.Api.Application.DTOs.Groups;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
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
        result.Message.ShouldContain("cluster, state, city, state_city");
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
}
