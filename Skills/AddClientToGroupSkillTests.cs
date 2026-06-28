// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AddClientToGroupSkill: the membership is written inside a transaction and re-read from
/// the database; a confirmed read reports a verified success, a missing read rolls the write back and
/// reports an error instead of a false success, and an existing membership is rejected up front.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddClientToGroupSkillTests
{
    private IClientRepository _clientRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ICompanyClock _companyClock = null!;
    private AddClientToGroupSkill _skill = null!;

    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>())
            .Returns(new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc));
        _skill = new AddClientToGroupSkill(
            _clientRepository, _groupRepository, _groupItemRepository, _unitOfWork, _companyClock);

        _clientRepository.Exists(ClientId).Returns(true);
        _groupRepository.Get(GroupId).Returns(new Group { Id = GroupId, Name = "Bern" });
        _groupItemRepository.GetByClientAndGroup(ClientId, GroupId).Returns((GroupItem?)null);

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<Guid>>>())
            .Returns(ci => ci.Arg<Func<Task<Guid>>>()());
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients", "CanViewGroups" }
    };

    private Dictionary<string, object> Params() => new()
    {
        ["clientId"] = ClientId.ToString(),
        ["groupId"] = GroupId.ToString(),
        ["validFrom"] = "2026-05-01"
    };

    [Test]
    public async Task Adds_AndReportsVerified_WhenPersistenceIsConfirmed()
    {
        _groupItemRepository.GetNoTracking(Arg.Any<Guid>())
            .Returns(ci => new GroupItem { Id = ci.Arg<Guid>(), ClientId = ClientId, GroupId = GroupId });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("verified"));
        await _groupItemRepository.Received(1).Add(
            Arg.Is<GroupItem>(gi => gi.ClientId == ClientId && gi.GroupId == GroupId));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task ReturnsError_AndRollsBack_WhenPersistenceCannotBeConfirmed()
    {
        _groupItemRepository.GetNoTracking(Arg.Any<Guid>()).Returns((GroupItem?)null);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("rolled back"));
        await _groupItemRepository.Received(1).Add(Arg.Any<GroupItem>());
    }

    [Test]
    public async Task Rejects_WhenClientIsAlreadyAMember()
    {
        _groupItemRepository.GetByClientAndGroup(ClientId, GroupId)
            .Returns(new GroupItem { Id = Guid.NewGuid(), ClientId = ClientId, GroupId = GroupId });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("already a member"));
        await _groupItemRepository.DidNotReceive().Add(Arg.Any<GroupItem>());
    }

    [Test]
    public async Task AsksForStartDate_AndDoesNotPersist_WhenValidFromIsMissing()
    {
        var parameters = new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["groupId"] = GroupId.ToString()
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("date"));
        await _groupItemRepository.DidNotReceive().Add(Arg.Any<GroupItem>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }
}
