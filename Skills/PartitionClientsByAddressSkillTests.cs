// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for PartitionClientsByAddressSkill: entityType=Customer and any unrecognised entityType
/// or level are rejected before the command is ever sent, a restricted group scope is refused (this
/// skill creates groups at the top of the tree), an unresolvable rootGroupName is rejected with the
/// real group names, and a valid call resolves rootGroupName and forwards apply/level/entityType as given.
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
                    cmd.Apply, cmd.Level.ToString(), cmd.EntityType.ToString(), 1, 0, 0,
                    cmd.Apply ? 1 : 0, cmd.Apply ? 1 : 0, 0,
                    new List<PartitionGroupSummary> { new("BE", null, false, null, 1) },
                    new List<Klacks.Api.Application.DTOs.Grouping.UnassignablePartitionClient>(),
                    new List<string>()));
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
    public async Task ReturnsError_WhenEntityTypeIsCustomer()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["entityType"] = "Customer" });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Customer"));
        await _mediator.DidNotReceive().Send(Arg.Any<PartitionClientsByAddressCommand>(), Arg.Any<CancellationToken>());
    }

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
                cmd.Level == GroupPartitionLevelEnum.CantonCity &&
                cmd.EntityType == EntityTypeEnum.Employee &&
                cmd.RootGroupId == HeadOfficeGroupId),
            Arg.Any<CancellationToken>());
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
}
