// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GetGroupDetailsSkill — lookup by id and by name, tree path and member
/// count in the result, missing identification and out-of-scope refusal.
/// </summary>

using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Groups;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetGroupDetailsSkillTests
{
    private IGroupRepository _groupRepository = null!;
    private IMediator _mediator = null!;
    private Group _group = null!;
    private Group _root = null!;

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _mediator = Substitute.For<IMediator>();

        _root = new Group { Id = Guid.NewGuid(), Name = "Verkauf" };
        _group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Filiale Bern",
            Parent = _root.Id,
            ValidFrom = new DateTime(2026, 1, 1),
            PaymentInterval = PaymentInterval.Weekly
        };

        _groupRepository.List().Returns(new List<Group> { _root, _group });
        _groupRepository.Get(_group.Id).Returns(_group);
        _groupRepository.GetPath(_group.Id).Returns(new[] { _root, _group });
        _groupRepository.GetChildren(_group.Id).Returns(Array.Empty<Group>());
        _mediator.Send(Arg.Any<GetGroupMembersQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<GroupItemResource>
            {
                new() { ClientId = Guid.NewGuid() },
                new() { ClientId = Guid.NewGuid() },
                new() { ShiftId = Guid.NewGuid() }
            });
    }

    private GetGroupDetailsSkill Skill(IGroupScopeGuard? guard = null) =>
        new(_groupRepository, guard ?? TestGroupScopeGuard.Unrestricted(), _mediator);

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    [Test]
    public async Task ReturnsDetails_ById_WithPathAndClientMemberCount()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupId"] = _group.Id.ToString()
        });

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Message, Does.Contain("Verkauf > Filiale Bern"));
        Assert.That(result.Message, Does.Contain("2 client member(s)"));
        Assert.That(result.Message, Does.Contain("Weekly"));
    }

    [Test]
    public async Task ReturnsDetails_ByName()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupName"] = "Filiale Bern"
        });

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Message, Does.Contain("Filiale Bern"));
    }

    [Test]
    public async Task ReturnsError_WhenNoIdentificationSupplied()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("groupId or groupName"));
    }

    [Test]
    public async Task ReturnsError_WhenGroupNotFoundById()
    {
        var missing = Guid.NewGuid();
        _groupRepository.Get(missing).Returns((Group?)null);

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupId"] = missing.ToString()
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not found"));
    }

    [Test]
    public async Task ReturnsScopeError_WhenGroupOutsideUserScope()
    {
        var scopedRootId = Guid.NewGuid();
        _group.Root = Guid.NewGuid();

        var result = await Skill(TestGroupScopeGuard.Restricted(new[] { scopedRootId }, "Verkauf"))
            .ExecuteAsync(Ctx(), new Dictionary<string, object>
            {
                ["groupId"] = _group.Id.ToString()
            });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("outside your assigned group scope"));
    }
}
