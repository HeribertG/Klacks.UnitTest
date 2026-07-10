// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ListGroupMembersSkill — client-only filtering, limit with truncation
/// note, empty group and lookup by name.
/// </summary>

using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Groups;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListGroupMembersSkillTests
{
    private IGroupRepository _groupRepository = null!;
    private IMediator _mediator = null!;
    private Group _group = null!;

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _mediator = Substitute.For<IMediator>();

        _group = new Group { Id = Guid.NewGuid(), Name = "Filiale Bern" };
        _groupRepository.List().Returns(new List<Group> { _group });
        _groupRepository.Get(_group.Id).Returns(_group);
    }

    private ListGroupMembersSkill Skill() =>
        new(_groupRepository, TestGroupScopeGuard.Unrestricted(), _mediator);

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    private static GroupItemResource ClientMember(string firstName, string lastName) => new()
    {
        ClientId = Guid.NewGuid(),
        Client = new ClientResource { Id = Guid.NewGuid(), FirstName = firstName, Name = lastName }
    };

    [Test]
    public async Task ListsOnlyClientMembers_IgnoringShiftAssignments()
    {
        _mediator.Send(Arg.Any<GetGroupMembersQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<GroupItemResource>
            {
                ClientMember("Anna", "Müller"),
                ClientMember("Beat", "Keller"),
                new() { ShiftId = Guid.NewGuid() }
            });

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupName"] = "Filiale Bern"
        });

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Message, Does.Contain("2 client member(s)"));
    }

    [Test]
    public async Task TruncatesToLimit_AndSaysSo()
    {
        _mediator.Send(Arg.Any<GetGroupMembersQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<GroupItemResource>
            {
                ClientMember("Anna", "Müller"),
                ClientMember("Beat", "Keller"),
                ClientMember("Carla", "Steiner")
            });

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupId"] = _group.Id.ToString(),
            ["limit"] = 2
        });

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Message, Does.Contain("first 2 of 3"));
    }

    [Test]
    public async Task ReportsEmptyGroup()
    {
        _mediator.Send(Arg.Any<GetGroupMembersQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<GroupItemResource>());

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupId"] = _group.Id.ToString()
        });

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Message, Does.Contain("0 client member(s)"));
    }

    [Test]
    public async Task ReturnsError_WhenGroupNameUnknown()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupName"] = "Gibt Es Nicht"
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not found"));
    }
}
