// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MoveGroupSkill — name resolution, self-move and descendant guards,
/// already-under-parent no-op, out-of-scope refusal, verified happy path and the
/// rollback path when the database re-read does not confirm the new parent.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class MoveGroupSkillTests
{
    private IGroupRepository _groupRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private Group _child = null!;
    private Group _oldParent = null!;
    private Group _newParent = null!;

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(ci => ci.Arg<Func<Task<bool>>>()());

        _oldParent = new Group { Id = Guid.NewGuid(), Name = "Verkauf" };
        _newParent = new Group { Id = Guid.NewGuid(), Name = "Logistik" };
        _child = new Group { Id = Guid.NewGuid(), Name = "Filiale Bern", Parent = _oldParent.Id };

        _groupRepository.List().Returns(new List<Group> { _oldParent, _newParent, _child });
        _groupRepository.Get(_child.Id).Returns(_child);
        _groupRepository.Get(_oldParent.Id).Returns(_oldParent);
        _groupRepository.Get(_newParent.Id).Returns(_newParent);
        _groupRepository.GetPath(_newParent.Id).Returns(new[] { _newParent });
        _groupRepository.GetPath(_child.Id).Returns(new[] { _newParent, _child });
        _groupRepository.MoveNode(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns(Task.CompletedTask);
        _groupRepository.GetNoTracking(_child.Id).Returns(_child);
    }

    private MoveGroupSkill Skill(IGroupScopeGuard? guard = null) =>
        new(_groupRepository, guard ?? TestGroupScopeGuard.Unrestricted(), _unitOfWork);

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditSettings" }
    };

    [Test]
    public async Task MovesGroupByName_AndReportsVerifiedWithNewPath()
    {
        _groupRepository.MoveNode(_child.Id, _newParent.Id)
            .Returns(Task.CompletedTask)
            .AndDoes(_ => _child.Parent = _newParent.Id);

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupName"] = "Filiale Bern",
            ["newParentName"] = "Logistik"
        });

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Message, Does.Contain("verified"));
        Assert.That(result.Message, Does.Contain("Logistik > Filiale Bern"));
        await _groupRepository.Received(1).MoveNode(_child.Id, _newParent.Id);
    }

    [Test]
    public async Task ReturnsError_WhenMovingGroupUnderItself()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupId"] = _child.Id.ToString(),
            ["newParentId"] = _child.Id.ToString()
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("under itself"));
        await _groupRepository.DidNotReceive().MoveNode(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Test]
    public async Task ReturnsError_WhenTargetIsOwnDescendant()
    {
        _groupRepository.GetPath(_newParent.Id).Returns(new[] { _child, _newParent });

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupId"] = _child.Id.ToString(),
            ["newParentId"] = _newParent.Id.ToString()
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("descendant"));
        await _groupRepository.DidNotReceive().MoveNode(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Test]
    public async Task ReturnsNoOp_WhenGroupAlreadyUnderTargetParent()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupId"] = _child.Id.ToString(),
            ["newParentId"] = _oldParent.Id.ToString()
        });

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("nothing to move"));
        await _groupRepository.DidNotReceive().MoveNode(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Test]
    public async Task ReturnsError_WhenGroupNameAmbiguous()
    {
        var twin = new Group { Id = Guid.NewGuid(), Name = "Filiale Bern Ost" };
        var twin2 = new Group { Id = Guid.NewGuid(), Name = "Filiale Bern West" };
        _groupRepository.List().Returns(new List<Group> { _oldParent, _newParent, twin, twin2 });

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupName"] = "Filiale",
            ["newParentName"] = "Logistik"
        });

        Assert.That(result.Success, Is.False);
        await _groupRepository.DidNotReceive().MoveNode(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Test]
    public async Task ReturnsScopeError_WhenGroupOutsideUserScope()
    {
        var scopedRootId = Guid.NewGuid();
        _child.Root = Guid.NewGuid();
        _newParent.Root = scopedRootId;

        var result = await Skill(TestGroupScopeGuard.Restricted(new[] { scopedRootId }, "Verkauf"))
            .ExecuteAsync(Ctx(), new Dictionary<string, object>
            {
                ["groupId"] = _child.Id.ToString(),
                ["newParentId"] = _newParent.Id.ToString()
            });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("outside your assigned group scope"));
        await _groupRepository.DidNotReceive().MoveNode(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Test]
    public async Task ReturnsError_WhenVerificationRereadShowsOldParent()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupId"] = _child.Id.ToString(),
            ["newParentId"] = _newParent.Id.ToString()
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("verification failed"));
    }

    [Test]
    public async Task ReturnsError_WhenVerificationRereadIsMissing()
    {
        _groupRepository.GetNoTracking(_child.Id).Returns((Group?)null);

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupId"] = _child.Id.ToString(),
            ["newParentId"] = _newParent.Id.ToString()
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("verification failed"));
    }
}
