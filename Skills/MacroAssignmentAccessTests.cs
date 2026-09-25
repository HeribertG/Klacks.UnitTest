// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MacroAssignmentAccess: only the Admin role itself passes; CanEditSettings without the role does not.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class MacroAssignmentAccessTests
{
    private static SkillExecutionContext Context(params string[] rights) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "caller",
        UserPermissions = rights
    };

    [Test]
    public void AdminRole_Passes()
    {
        MacroAssignmentAccess.IsAdmin(Context(Roles.Admin, Permissions.CanEditSettings)).ShouldBeTrue();
    }

    [Test]
    public void EditSettingsWithoutTheAdminRole_DoesNotPass()
    {
        MacroAssignmentAccess.IsAdmin(Context(Permissions.CanEditSettings)).ShouldBeFalse();
    }
}
