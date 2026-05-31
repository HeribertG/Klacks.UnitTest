// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for assign_user_permissions: the skill resolves the target user (by id or name),
/// applies the requested role level exclusively via ChangeRoleCommand (grant target, revoke the
/// other elevated role; User removes both), and reports the derived permission set.
/// </summary>

using Klacks.Api.Application.Commands.Accounts;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.DTOs.Registrations;
using Klacks.Api.Domain.Interfaces.Authentification;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AssignUserPermissionsSkillTests
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
    public async Task AssignAdmin_ByUserId_GrantsAdmin_RevokesAuthorised()
    {
        var mediator = Substitute.For<IMediator>();
        var users = Substitute.For<IUserManagementService>();
        users.FindUserByIdAsync("u1").Returns(User("u1"));
        var skill = new AssignUserPermissionsSkill(mediator, users);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["userId"] = "u1",
            ["role"] = Roles.Admin
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<ChangeRoleCommand>(c =>
                c.ChangeRole.UserId == "u1" && c.ChangeRole.RoleName == Roles.Admin && c.ChangeRole.IsSelected),
            Arg.Any<CancellationToken>());
        await mediator.Received(1).Send(
            Arg.Is<ChangeRoleCommand>(c =>
                c.ChangeRole.UserId == "u1" && c.ChangeRole.RoleName == Roles.Authorised && !c.ChangeRole.IsSelected),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AssignUser_RemovesBothElevatedRoles_GrantsNone()
    {
        var mediator = Substitute.For<IMediator>();
        var users = Substitute.For<IUserManagementService>();
        users.FindUserByIdAsync("u1").Returns(User("u1"));
        var skill = new AssignUserPermissionsSkill(mediator, users);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["userId"] = "u1",
            ["role"] = Roles.User
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<ChangeRoleCommand>(c => c.ChangeRole.RoleName == Roles.Admin && !c.ChangeRole.IsSelected),
            Arg.Any<CancellationToken>());
        await mediator.Received(1).Send(
            Arg.Is<ChangeRoleCommand>(c => c.ChangeRole.RoleName == Roles.Authorised && !c.ChangeRole.IsSelected),
            Arg.Any<CancellationToken>());
        await mediator.DidNotReceive().Send(
            Arg.Is<ChangeRoleCommand>(c => c.ChangeRole.IsSelected), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AssignAuthorised_ByUserName_ResolvesUser_GrantsAuthorised_RevokesAdmin()
    {
        var mediator = Substitute.For<IMediator>();
        var users = Substitute.For<IUserManagementService>();
        users.GetUserListAsync().Returns(new List<UserResource>
        {
            new() { Id = "u9", FirstName = "Anna", LastName = "Muster", Email = "anna@x.io", UserName = "amuster" }
        });
        users.FindUserByIdAsync("u9").Returns(User("u9"));
        var skill = new AssignUserPermissionsSkill(mediator, users);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["userName"] = "Anna",
            ["role"] = Roles.Authorised
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<ChangeRoleCommand>(c =>
                c.ChangeRole.UserId == "u9" && c.ChangeRole.RoleName == Roles.Authorised && c.ChangeRole.IsSelected),
            Arg.Any<CancellationToken>());
        await mediator.Received(1).Send(
            Arg.Is<ChangeRoleCommand>(c =>
                c.ChangeRole.UserId == "u9" && c.ChangeRole.RoleName == Roles.Admin && !c.ChangeRole.IsSelected),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InvalidRole_ReturnsError_NoMutation()
    {
        var mediator = Substitute.For<IMediator>();
        var users = Substitute.For<IUserManagementService>();
        users.FindUserByIdAsync("u1").Returns(User("u1"));
        var skill = new AssignUserPermissionsSkill(mediator, users);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["userId"] = "u1",
            ["role"] = "Superuser"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<ChangeRoleCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AmbiguousUserName_ReturnsError_NoMutation()
    {
        var mediator = Substitute.For<IMediator>();
        var users = Substitute.For<IUserManagementService>();
        users.GetUserListAsync().Returns(new List<UserResource>
        {
            new() { Id = "u1", FirstName = "Anna", LastName = "Muster", Email = "anna@x.io", UserName = "amuster" },
            new() { Id = "u2", FirstName = "Anna", LastName = "Berg", Email = "annab@x.io", UserName = "aberg" }
        });
        var skill = new AssignUserPermissionsSkill(mediator, users);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["userName"] = "Anna",
            ["role"] = Roles.Admin
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<ChangeRoleCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownUserId_ReturnsError_NoMutation()
    {
        var mediator = Substitute.For<IMediator>();
        var users = Substitute.For<IUserManagementService>();
        users.FindUserByIdAsync("missing").Returns((AppUser?)null);
        var skill = new AssignUserPermissionsSkill(mediator, users);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["userId"] = "missing",
            ["role"] = Roles.Admin
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<ChangeRoleCommand>(), Arg.Any<CancellationToken>());
    }
}
