// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for McpPrincipalCapper: an Admin-owned principal must be downgraded to Authorised
/// for the MCP endpoint, while every other principal shape passes through unchanged.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Presentation.Mcp;

namespace Klacks.UnitTest.Mcp;

[TestFixture]
public class McpPrincipalCapperTests
{
    [Test]
    public void AdminOnlyPrincipal_IsDowngradedToAuthorised()
    {
        var principal = McpTestData.Principal(Guid.NewGuid(), Guid.NewGuid(), "admin-user", Roles.Admin);

        var capped = McpPrincipalCapper.CapToAuthorised(principal);

        Assert.That(capped.IsInRole(Roles.Admin), Is.False);
        Assert.That(capped.IsInRole(Roles.Authorised), Is.True);
    }

    [Test]
    public void AdminAndAuthorisedPrincipal_DropsAdminKeepsSingleAuthorisedClaim()
    {
        var principal = McpTestData.Principal(Guid.NewGuid(), Guid.NewGuid(), "admin-user", Roles.Admin, Roles.Authorised);

        var capped = McpPrincipalCapper.CapToAuthorised(principal);

        Assert.That(capped.IsInRole(Roles.Admin), Is.False);
        Assert.That(capped.FindAll(ClaimTypes.Role).Count(claim => claim.Value == Roles.Authorised), Is.EqualTo(1));
    }

    [Test]
    public void AuthorisedPrincipal_PassesThroughUnchanged()
    {
        var principal = McpTestData.Principal(Guid.NewGuid(), Guid.NewGuid(), "supervisor", Roles.Authorised);

        var capped = McpPrincipalCapper.CapToAuthorised(principal);

        Assert.That(capped, Is.SameAs(principal));
    }

    [Test]
    public void PrincipalWithoutRoles_PassesThroughUnchanged()
    {
        var principal = McpTestData.Principal(Guid.NewGuid(), Guid.NewGuid(), "nobody");

        var capped = McpPrincipalCapper.CapToAuthorised(principal);

        Assert.That(capped, Is.SameAs(principal));
    }

    [Test]
    public void UnauthenticatedPrincipal_PassesThroughUnchanged()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        var capped = McpPrincipalCapper.CapToAuthorised(principal);

        Assert.That(capped, Is.SameAs(principal));
    }

    [Test]
    public void AdminPrincipal_PreservesNonRoleClaims()
    {
        var userId = Guid.NewGuid();
        var principal = McpTestData.Principal(userId, Guid.NewGuid(), "admin-user", Roles.Admin);

        var capped = McpPrincipalCapper.CapToAuthorised(principal);

        Assert.That(capped.FindFirst(ClaimTypes.NameIdentifier)?.Value, Is.EqualTo(userId.ToString()));
        Assert.That(capped.FindFirst(ClaimTypes.Name)?.Value, Is.EqualTo("admin-user"));
    }
    [Test]
    public void AdminPersonalAccessToken_KeepsSchemeAndAccessModeAfterCap()
    {
        var principal = McpTestData.PatPrincipal(
            Guid.NewGuid(), Guid.NewGuid(), PersonalAccessTokenAccessMode.Read, Roles.Admin);

        var capped = McpPrincipalCapper.CapToAuthorised(principal);

        Assert.That(capped.IsInRole(Roles.Admin), Is.False);
        Assert.That(capped.IsInRole(Roles.Authorised), Is.True);
        Assert.That(capped.Identity?.AuthenticationType, Is.EqualTo(PatConstants.SchemeName));
        Assert.That(McpAccessModeResolver.Resolve(capped), Is.EqualTo(PersonalAccessTokenAccessMode.Read));
    }

    [Test]
    public void AdminPrincipalWithCustomRoleClaimType_DropsAdminUnderThatType()
    {
        const string roleClaimType = "role";
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(roleClaimType, Roles.Admin)],
            "Bearer",
            ClaimTypes.Name,
            roleClaimType);
        var principal = new ClaimsPrincipal(identity);

        var capped = McpPrincipalCapper.CapToAuthorised(principal);

        Assert.That(capped.IsInRole(Roles.Admin), Is.False);
        Assert.That(capped.IsInRole(Roles.Authorised), Is.True);
    }

    [Test]
    public void AdminPrincipalWithSeveralIdentities_DropsAdminFromEveryIdentity()
    {
        var first = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, Roles.Admin)],
            "Bearer");
        var second = new ClaimsIdentity([new Claim(ClaimTypes.Role, Roles.Admin)], PatConstants.SchemeName);
        var principal = new ClaimsPrincipal([first, second]);

        var capped = McpPrincipalCapper.CapToAuthorised(principal);

        Assert.That(capped.IsInRole(Roles.Admin), Is.False);
        Assert.That(capped.FindAll(ClaimTypes.Role).Count(claim => claim.Value == Roles.Authorised), Is.EqualTo(1));
        Assert.That(capped.Identities.Select(i => i.AuthenticationType), Is.EqualTo(new[] { "Bearer", PatConstants.SchemeName }));
    }
}
