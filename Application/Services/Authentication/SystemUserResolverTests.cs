// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the shared account resolution and role parsing of the user-role skills: id wins over name, a name
/// matches first name, last name, e-mail, login and full name, an ambiguous name yields candidates that only the
/// caller that asks for them gets worded, and role levels are matched case-insensitively to canonical names.
/// </summary>

using Klacks.Api.Application.Services.Authentication;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;

namespace Klacks.UnitTest.Application.Services.Authentication;

[TestFixture]
public class SystemUserResolverTests
{
    private static UserResource Account(string id, string first, string last) => new()
    {
        Id = id,
        FirstName = first,
        LastName = last,
        Email = $"{first.ToLowerInvariant()}@example.test",
        UserName = $"{first}{last}"
    };

    [Test]
    public async Task IdWinsOverName_AndSkipsTheAccountList()
    {
        var users = Substitute.For<IUserManagementService>();
        users.FindUserByIdAsync("u1").Returns(new AppUser { Id = "u1" });

        var resolution = await SystemUserResolver.ResolveAsync(users, "u1", "somebody else");

        resolution.Status.ShouldBe(SystemUserResolutionStatus.Resolved);
        resolution.User!.Id.ShouldBe("u1");
        await users.DidNotReceive().GetUserListAsync();
    }

    [TestCase("anna")]
    [TestCase("MUSTER")]
    [TestCase("anna@example")]
    [TestCase("AnnaMuster")]
    [TestCase("Anna Muster")]
    public async Task NameMatchesFirstLastMailLoginAndFullName(string term)
    {
        var users = Substitute.For<IUserManagementService>();
        users.GetUserListAsync().Returns(new List<UserResource> { Account("u1", "Anna", "Muster"), Account("u2", "Bruno", "Berg") });
        users.FindUserByIdAsync("u1").Returns(new AppUser { Id = "u1" });

        var resolution = await SystemUserResolver.ResolveAsync(users, null, term);

        resolution.Status.ShouldBe(SystemUserResolutionStatus.Resolved);
        resolution.User!.Id.ShouldBe("u1");
    }

    [Test]
    public async Task AmbiguousName_WordsCandidatesOnlyWhenAsked()
    {
        var users = Substitute.For<IUserManagementService>();
        users.GetUserListAsync().Returns(new List<UserResource> { Account("u1", "Anna", "Muster"), Account("u2", "Anna", "Berg") });

        var resolution = await SystemUserResolver.ResolveAsync(users, null, "Anna");

        resolution.Status.ShouldBe(SystemUserResolutionStatus.Ambiguous);
        resolution.ErrorMessage(listCandidates: true).ShouldContain("anna@example.test");
        resolution.ErrorMessage(listCandidates: true).ShouldContain("Anna Berg");
        resolution.ErrorMessage(listCandidates: false).ShouldBe("'Anna' matches 2 accounts. Provide userId instead.");
    }

    [Test]
    public async Task MatchedButNotLoadable_IsLoadFailed()
    {
        var users = Substitute.For<IUserManagementService>();
        users.GetUserListAsync().Returns(new List<UserResource> { Account("u1", "Anna", "Muster") });
        users.FindUserByIdAsync("u1").Returns((AppUser?)null);

        var resolution = await SystemUserResolver.ResolveAsync(users, null, "Anna");

        resolution.Status.ShouldBe(SystemUserResolutionStatus.LoadFailed);
        resolution.ErrorMessage(listCandidates: false).ShouldBe("User 'Anna' could not be loaded.");
    }

    [TestCase("admin", Roles.Admin)]
    [TestCase("AUTHORISED", Roles.Authorised)]
    [TestCase("User", Roles.User)]
    [TestCase("Authorized", null)]
    [TestCase("  ", null)]
    [TestCase(null, null)]
    public void RoleLevel_IsMatchedCaseInsensitively_ToTheCanonicalName(string? input, string? expected)
    {
        AssignableRoleParser.Normalize(input).ShouldBe(expected);
    }
}
