// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for get_user_group_scope: read-only counterpart to set_user_group_scope. Admins
/// report all root groups (scope cannot be restricted for them); non-admins report exactly their
/// assigned root groups, or an explicit "no scope configured" message when the list is empty.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Authentification;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Authentification;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetUserGroupScopeSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Roles.Admin, Permissions.CanEditSettings }
    };

    private static AppUser User(string id) => new()
    {
        Id = id,
        FirstName = "Anna",
        LastName = "Muster"
    };

    [Test]
    public async Task AdminUser_ReturnsAllRootGroups()
    {
        var users = Substitute.For<IUserManagementService>();
        var groups = Substitute.For<IGroupRepository>();
        var visibilities = Substitute.For<IGroupVisibilityRepository>();
        users.FindUserByIdAsync("u1").Returns(User("u1"));
        users.IsUserInRoleAsync(Arg.Any<AppUser>(), Roles.Admin).Returns(true);
        groups.GetRoots().Returns(new List<Group>
        {
            new() { Id = Guid.NewGuid(), Name = "Deutschweiz Mitte" },
            new() { Id = Guid.NewGuid(), Name = "Romandie" }
        });
        var skill = new GetUserGroupScopeSkill(users, groups, visibilities);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["userId"] = "u1" });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("Admin");
        result.Message.ShouldContain("all 2 groups");
        await visibilities.DidNotReceive().GetGroupVisibilityList();
    }

    [Test]
    public async Task NonAdminUser_WithAssignedGroups_ReturnsThem()
    {
        var users = Substitute.For<IUserManagementService>();
        var groups = Substitute.For<IGroupRepository>();
        var visibilities = Substitute.For<IGroupVisibilityRepository>();
        var groupId = Guid.NewGuid();
        users.FindUserByIdAsync("u1").Returns(User("u1"));
        users.IsUserInRoleAsync(Arg.Any<AppUser>(), Roles.Admin).Returns(false);
        groups.GetRoots().Returns(new List<Group>
        {
            new() { Id = groupId, Name = "Deutschweiz Mitte" },
            new() { Id = Guid.NewGuid(), Name = "Romandie" }
        });
        visibilities.GetGroupVisibilityList().Returns(new List<GroupVisibility>
        {
            new() { AppUserId = "u1", GroupId = groupId },
            new() { AppUserId = "someone-else", GroupId = Guid.NewGuid() }
        });
        var skill = new GetUserGroupScopeSkill(users, groups, visibilities);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["userId"] = "u1" });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("Deutschweiz Mitte");
        result.Message.ShouldNotContain("Romandie");
    }

    [Test]
    public async Task NonAdminUser_WithNoAssignedGroups_ReturnsExplicitEmptyMessage()
    {
        var users = Substitute.For<IUserManagementService>();
        var groups = Substitute.For<IGroupRepository>();
        var visibilities = Substitute.For<IGroupVisibilityRepository>();
        users.FindUserByIdAsync("u1").Returns(User("u1"));
        users.IsUserInRoleAsync(Arg.Any<AppUser>(), Roles.Admin).Returns(false);
        groups.GetRoots().Returns(new List<Group> { new() { Id = Guid.NewGuid(), Name = "Deutschweiz Mitte" } });
        visibilities.GetGroupVisibilityList().Returns(new List<GroupVisibility>());
        var skill = new GetUserGroupScopeSkill(users, groups, visibilities);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["userId"] = "u1" });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("no group scope configured");
    }

    [Test]
    public async Task UnknownUserId_ReturnsError()
    {
        var users = Substitute.For<IUserManagementService>();
        var groups = Substitute.For<IGroupRepository>();
        var visibilities = Substitute.For<IGroupVisibilityRepository>();
        users.FindUserByIdAsync("missing").Returns((AppUser?)null);
        var skill = new GetUserGroupScopeSkill(users, groups, visibilities);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["userId"] = "missing" });

        result.Success.ShouldBeFalse();
        await groups.DidNotReceive().GetRoots();
    }
}
