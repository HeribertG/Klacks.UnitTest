// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClaimsPrincipal.GetUserRights — the single reader that replaced five copies of the
/// role expansion. It must yield the role string itself (the skill executor's admin bypass matches on
/// it) plus the granular permissions of every role claim. The regression it locks down: the skill API
/// returned role strings only, which left every non-admin failing permission checks.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Extensions;

namespace Klacks.UnitTest.Presentation.Extensions;

[TestFixture]
public class ClaimsPrincipalExtensionsTests
{
    private static ClaimsPrincipal WithRoles(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(r => new Claim(ClaimTypes.Role, r)), "test"));

    [Test]
    public void GetUserRights_Authorised_ExpandsToGranularPermissions()
    {
        var rights = WithRoles(Roles.Authorised).GetUserRights();

        rights.ShouldContain(Roles.Authorised);
        rights.ShouldContain(Permissions.CanEditClients);
        rights.ShouldContain(Permissions.CanViewShifts);
    }

    [Test]
    public void GetUserRights_Authorised_PassesAPermissionCheck()
    {
        var rights = WithRoles(Roles.Authorised).GetUserRights();

        Permissions.HasAllRequiredPermissions(rights, Permissions.CanEditClients).ShouldBeTrue();
    }

    [Test]
    public void GetUserRights_Admin_KeepsTheRoleStringForTheBypass()
    {
        var rights = WithRoles(Roles.Admin).GetUserRights();

        rights.ShouldContain(Roles.Admin);
    }

    [Test]
    public void GetUserRights_NoRoleClaims_IsEmpty()
    {
        new ClaimsPrincipal(new ClaimsIdentity()).GetUserRights().ShouldBeEmpty();
    }
}
