// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for evaluate_user_role_change: the skill resolves the account (by id or name), reads its roles and the
/// active administrator count, relays the advisor's evaluation and changes nothing. A missing, unknown or ambiguous
/// account is refused without naming other accounts, and the result carries no e-mail address.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Authentification;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.DTOs.Registrations;
using Klacks.Api.Domain.Interfaces.Authentification;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Authentification;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class EvaluateUserRoleChangeSkillTests
{
    private const string AnnaEmail = "anna.secret@example.test";

    private static SkillExecutionContext Ctx(Guid? userId = null) => new()
    {
        UserId = userId ?? Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Roles.Admin, Permissions.CanEditSettings }
    };

    private static AppUser Anna(string id) => new() { Id = id, FirstName = "Anna", LastName = "Muster", Email = AnnaEmail };

    private static UserResource Account(string id, string first, string last, bool admin) => new()
    {
        Id = id,
        FirstName = first,
        LastName = last,
        Email = id == "u1" ? AnnaEmail : $"{first}.{last}@example.test",
        UserName = $"{first}{last}",
        IsAdmin = admin
    };

    private static IUserManagementService Users(AppUser? found, IList<string> roles, params UserResource[] accounts)
    {
        var users = Substitute.For<IUserManagementService>();
        users.FindUserByIdAsync(Arg.Any<string>()).Returns(found);
        users.GetUserRolesAsync(Arg.Any<AppUser>()).Returns(roles);
        users.GetUserListAsync().Returns(accounts.ToList());
        users.CountActiveAdministratorsAsync().Returns(accounts.Count(account => account.IsAdmin));
        return users;
    }

    [Test]
    public async Task AdminToAuthorised_ById_ReturnsLostRights_AndChangesNothing()
    {
        var users = Users(Anna("u1"), new List<string> { Roles.Admin },
            Account("u1", "Anna", "Muster", true), Account("u2", "Bruno", "Berg", true));
        var skill = new EvaluateUserRoleChangeSkill(users);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["userId"] = "u1",
            ["role"] = "authorised"
        });

        result.Success.ShouldBeTrue();
        var evaluation = result.Data.ShouldBeOfType<UserRoleChangeEvaluation>();
        evaluation.CurrentRole.ShouldBe(Roles.Admin);
        evaluation.TargetRole.ShouldBe(Roles.Authorised);
        evaluation.ActiveAdministratorCount.ShouldBe(2);
        evaluation.RightsLost.ShouldContain(Permissions.CanEditSettings);
        result.Message.ShouldBe(evaluation.Recommendation);
        await users.DidNotReceive().ChangeUserRoleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Test]
    public async Task OnlyAdministrator_ToUser_ByName_IsBlocking()
    {
        var users = Users(Anna("u1"), new List<string> { Roles.Admin },
            Account("u1", "Anna", "Muster", true), Account("u2", "Bruno", "Berg", false));
        var skill = new EvaluateUserRoleChangeSkill(users);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["userName"] = "Anna",
            ["role"] = Roles.User
        });

        result.Success.ShouldBeTrue();
        var evaluation = result.Data.ShouldBeOfType<UserRoleChangeEvaluation>();
        evaluation.ActiveAdministratorCount.ShouldBe(1);
        evaluation.Findings[0].Code.ShouldBe(UserRoleChangeFindingCode.LastAdministratorDemotion);
        evaluation.Findings[0].IsBlocking.ShouldBeTrue();
    }

    [Test]
    public async Task CallerEvaluatingTheirOwnAccount_IsRecognisedByTheContextUserId()
    {
        var callerId = Guid.NewGuid();
        var users = Users(Anna(callerId.ToString()), new List<string> { Roles.Admin },
            Account(callerId.ToString(), "Anna", "Muster", true), Account("u2", "Bruno", "Berg", true));
        var skill = new EvaluateUserRoleChangeSkill(users);

        var result = await skill.ExecuteAsync(Ctx(callerId), new Dictionary<string, object>
        {
            ["userId"] = callerId.ToString(),
            ["role"] = Roles.Authorised
        });

        var evaluation = result.Data.ShouldBeOfType<UserRoleChangeEvaluation>();
        evaluation.Findings.ShouldContain(finding => finding.Code == UserRoleChangeFindingCode.OwnAccountLosesSettingsAccess);
    }

    [Test]
    public async Task Result_ContainsNoEmailAndNoOtherAccount()
    {
        var users = Users(Anna("u1"), new List<string> { Roles.Authorised },
            Account("u1", "Anna", "Muster", false), Account("u2", "Bruno", "Berg", true));
        var skill = new EvaluateUserRoleChangeSkill(users);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["userId"] = "u1",
            ["role"] = Roles.Admin
        });

        var serialised = JsonSerializer.Serialize(result.Data) + result.Message;
        serialised.ShouldNotContain(AnnaEmail);
        serialised.ShouldNotContain("@");
        serialised.ShouldNotContain("Bruno");
        serialised.ShouldNotContain("Berg");
    }

    [Test]
    public async Task UnknownUserId_ReturnsError_WithoutReadingRoles()
    {
        var users = Users(null, new List<string>());
        var skill = new EvaluateUserRoleChangeSkill(users);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["userId"] = "missing",
            ["role"] = Roles.Admin
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("User with id 'missing' not found.");
        await users.DidNotReceive().GetUserRolesAsync(Arg.Any<AppUser>());
    }

    [Test]
    public async Task UnknownUserName_ReturnsError()
    {
        var users = Users(null, new List<string>(), Account("u1", "Anna", "Muster", true));
        var skill = new EvaluateUserRoleChangeSkill(users);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["userName"] = "Zora",
            ["role"] = Roles.Admin
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("No user found matching 'Zora'.");
    }

    [Test]
    public async Task AmbiguousUserName_ReturnsError_WithoutNamingOrMailingAnyCandidate()
    {
        var users = Users(null, new List<string>(),
            Account("u1", "Anna", "Muster", true), Account("u2", "Anna", "Berg", false));
        var skill = new EvaluateUserRoleChangeSkill(users);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["userName"] = "Anna",
            ["role"] = Roles.Admin
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("'Anna' matches 2 accounts. Provide userId instead.");
        result.Message.ShouldNotContain("@");
        result.Message.ShouldNotContain("Berg");
        await users.DidNotReceive().GetUserRolesAsync(Arg.Any<AppUser>());
    }

    [Test]
    public async Task NeitherIdNorName_ReturnsError()
    {
        var skill = new EvaluateUserRoleChangeSkill(Users(null, new List<string>()));

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["role"] = Roles.Admin });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Either userId or userName must be provided.");
    }

    [TestCase("Superuser")]
    [TestCase("")]
    public async Task InvalidRole_ReturnsError_WithoutLookingUpTheAccount(string role)
    {
        var users = Users(Anna("u1"), new List<string>());
        var skill = new EvaluateUserRoleChangeSkill(users);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["userId"] = "u1",
            ["role"] = role
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Allowed values are: Admin, Authorised, User.");
        await users.DidNotReceive().FindUserByIdAsync(Arg.Any<string>());
    }
}
