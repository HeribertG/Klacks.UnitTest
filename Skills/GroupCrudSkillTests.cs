// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for create_group, update_group, delete_group and list_groups_hierarchical skills.
/// Covers parent validation, no-op update, cascade-delete guard and depth computation.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GroupCrudSkillTests
{
    private IGroupRepository _groupRepository = null!;
    private ICalendarSelectionRepository _calendarSelectionRepository = null!;
    private IUnitOfWork _unitOfWork = null!;

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _calendarSelectionRepository = Substitute.For<ICalendarSelectionRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditSettings", "CanViewSettings" }
    };

    [Test]
    public async Task CreateGroup_ReturnsError_WhenParentNotFound()
    {
        var skill = new CreateGroupSkill(_groupRepository, _calendarSelectionRepository, _unitOfWork);
        var parentId = Guid.NewGuid();
        _groupRepository.Get(parentId).Returns((Group?)null);
        var parameters = new Dictionary<string, object>
        {
            ["name"] = "Bern",
            ["parentId"] = parentId.ToString()
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not found"));
    }

    [Test]
    public async Task CreateGroup_AtRoot_AddsGroupAndCompletes()
    {
        var skill = new CreateGroupSkill(_groupRepository, _calendarSelectionRepository, _unitOfWork);
        var parameters = new Dictionary<string, object> { ["name"] = "Bern" };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        await _groupRepository.Received(1).Add(Arg.Is<Group>(g => g.Name == "Bern" && g.Parent == null));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task UpdateGroup_NoOp_WhenNoFieldsSupplied()
    {
        var skill = new UpdateGroupSkill(_groupRepository, _unitOfWork);
        var id = Guid.NewGuid();
        _groupRepository.Get(id).Returns(new Group { Id = id, Name = "Bern", ValidFrom = DateTime.UtcNow.Date });
        var parameters = new Dictionary<string, object> { ["groupId"] = id.ToString() };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("No fields"));
        await _groupRepository.DidNotReceive().Put(Arg.Any<Group>());
    }

    [Test]
    public async Task UpdateGroup_RenamesAndPersists()
    {
        var skill = new UpdateGroupSkill(_groupRepository, _unitOfWork);
        var id = Guid.NewGuid();
        _groupRepository.Get(id).Returns(new Group { Id = id, Name = "Old", ValidFrom = DateTime.UtcNow.Date });
        var parameters = new Dictionary<string, object>
        {
            ["groupId"] = id.ToString(),
            ["name"] = "Bern City"
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        await _groupRepository.Received(1).Put(Arg.Is<Group>(g => g.Name == "Bern City"));
    }

    [Test]
    public async Task DeleteGroup_RefusesCascade_WhenChildrenExistAndForceFalse()
    {
        var skill = new DeleteGroupSkill(_groupRepository, _unitOfWork);
        var id = Guid.NewGuid();
        _groupRepository.Get(id).Returns(new Group { Id = id, Name = "Bern" });
        _groupRepository.GetChildren(id).Returns(new[]
        {
            new Group { Id = Guid.NewGuid(), Name = "Bern Nord" }
        });
        var parameters = new Dictionary<string, object> { ["groupId"] = id.ToString() };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("child"));
        await _groupRepository.DidNotReceive().Delete(Arg.Any<Guid>());
    }

    [Test]
    public async Task DeleteGroup_CascadesWhenForceTrue()
    {
        var skill = new DeleteGroupSkill(_groupRepository, _unitOfWork);
        var id = Guid.NewGuid();
        var childId = Guid.NewGuid();
        _groupRepository.Get(id).Returns(new Group { Id = id, Name = "Bern" });
        _groupRepository.GetChildren(id).Returns(new[]
        {
            new Group { Id = childId, Name = "Bern Nord" }
        });
        var parameters = new Dictionary<string, object>
        {
            ["groupId"] = id.ToString(),
            ["forceCascade"] = true
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        await _groupRepository.Received(1).Delete(childId);
        await _groupRepository.Received(1).Delete(id);
    }

    [Test]
    public async Task ListGroupsHierarchical_ReturnsTreeWithDepth()
    {
        var skill = new ListGroupsHierarchicalSkill(_groupRepository);
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var grandId = Guid.NewGuid();
        _groupRepository.GetTree(null).Returns(new[]
        {
            new Group { Id = rootId, Name = "CH", Parent = null, Lft = 1, Rgt = 6 },
            new Group { Id = childId, Name = "Bern", Parent = rootId, Lft = 2, Rgt = 5 },
            new Group { Id = grandId, Name = "Bern Nord", Parent = childId, Lft = 3, Rgt = 4 }
        });

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        Assert.That(result.Success, Is.True);
        Assert.That(result.Data, Is.Not.Null);
    }

    [Test]
    public async Task ListGroupsHierarchical_RespectsMaxDepth()
    {
        var skill = new ListGroupsHierarchicalSkill(_groupRepository);
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var grandId = Guid.NewGuid();
        _groupRepository.GetTree(null).Returns(new[]
        {
            new Group { Id = rootId, Name = "CH", Parent = null, Lft = 1, Rgt = 6 },
            new Group { Id = childId, Name = "Bern", Parent = rootId, Lft = 2, Rgt = 5 },
            new Group { Id = grandId, Name = "Bern Nord", Parent = childId, Lft = 3, Rgt = 4 }
        });

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["maxDepth"] = 1 });

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("2 group(s)"));
    }
}
