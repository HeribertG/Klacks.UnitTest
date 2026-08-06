// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Verifies Permissions.HasAllRequiredPermissions: the comma-separated RequiredPermission string
/// stored on AgentSkill must require ALL listed permissions, not an exact-string match against a
/// single user right (regression for the skill-visibility bug where multi-permission skills were
/// unreachable for any non-admin user). Also verifies Permissions.ExpandRoles, the single expansion
/// every assistant entry point now shares.
/// </summary>

using Klacks.Api.Domain.Constants;
using Shouldly;

namespace Klacks.UnitTest.Domain.Constants;

[TestFixture]
public class PermissionsTests
{
    [Test]
    public void HasAllRequiredPermissions_NullRequirement_ReturnsTrue()
    {
        Permissions.HasAllRequiredPermissions(new List<string>(), null).ShouldBeTrue();
    }

    [Test]
    public void HasAllRequiredPermissions_EmptyRequirement_ReturnsTrue()
    {
        Permissions.HasAllRequiredPermissions(new List<string>(), string.Empty).ShouldBeTrue();
    }

    [Test]
    public void HasAllRequiredPermissions_SinglePermission_UserHasIt_ReturnsTrue()
    {
        var userRights = new List<string> { Permissions.CanViewGroups };

        Permissions.HasAllRequiredPermissions(userRights, Permissions.CanViewGroups).ShouldBeTrue();
    }

    [Test]
    public void HasAllRequiredPermissions_SinglePermission_UserLacksIt_ReturnsFalse()
    {
        var userRights = new List<string> { Permissions.CanViewClients };

        Permissions.HasAllRequiredPermissions(userRights, Permissions.CanViewGroups).ShouldBeFalse();
    }

    [Test]
    public void HasAllRequiredPermissions_CommaSeparated_UserHasBoth_ReturnsTrue()
    {
        var userRights = new List<string> { Permissions.CanEditClients, Permissions.CanViewGroups };

        Permissions.HasAllRequiredPermissions(userRights, "CanEditClients,CanViewGroups").ShouldBeTrue();
    }

    [Test]
    public void HasAllRequiredPermissions_CommaSeparated_UserHasOnlyOne_ReturnsFalse()
    {
        var userRights = new List<string> { Permissions.CanEditClients };

        Permissions.HasAllRequiredPermissions(userRights, "CanEditClients,CanViewGroups").ShouldBeFalse();
    }

    [Test]
    public void HasAllRequiredPermissions_CommaSeparated_AdminBypasses_ReturnsTrue()
    {
        var userRights = new List<string> { Roles.Admin };

        Permissions.HasAllRequiredPermissions(userRights, "CanEditClients,CanViewGroups").ShouldBeTrue();
    }

    [Test]
    public void ExpandRoles_KeepsTheRoleString_SoTheAdminBypassStillFires()
    {
        var rights = Permissions.ExpandRoles(new[] { Roles.Admin });

        rights.ShouldContain(Roles.Admin);
        Permissions.HasAllRequiredPermissions(rights, "CanEditClients,CanViewGroups").ShouldBeTrue();
    }

    [Test]
    public void ExpandRoles_AddsTheGranularPermissionsOfTheRole()
    {
        var rights = Permissions.ExpandRoles(new[] { Roles.Authorised });

        rights.ShouldContain(Permissions.CanEditClients);
        rights.ShouldContain(Permissions.CanPlan);
        rights.ShouldNotContain(Permissions.CanDeleteClients);
    }

    [Test]
    public void ExpandRoles_MultipleRoles_DoesNotDuplicate()
    {
        var rights = Permissions.ExpandRoles(new[] { Roles.Admin, Roles.Authorised });

        rights.Count(r => r == Permissions.CanEditClients).ShouldBe(1);
        rights.Count(r => r == Permissions.CanViewClients).ShouldBe(1);
    }

    [Test]
    public void ExpandRoles_NoRoles_YieldsNothing()
    {
        Permissions.ExpandRoles(Array.Empty<string>()).ShouldBeEmpty();
    }

}
