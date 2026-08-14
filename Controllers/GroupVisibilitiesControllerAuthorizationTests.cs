using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Controllers.UserBackend.Associations;
using Microsoft.AspNetCore.Authorization;
using Shouldly;
using System.Reflection;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class GroupVisibilitiesControllerAuthorizationTests
{
    [Test]
    public void Controller_ShouldRequireAdminRole()
    {
        var authorizeAttribute = typeof(GroupVisibilitiesController)
            .GetCustomAttribute<AuthorizeAttribute>(inherit: false);

        authorizeAttribute.ShouldNotBeNull(
            "GroupVisibilities is an admin-only settings endpoint and must carry a role gate");
        authorizeAttribute!.Roles.ShouldBe(Roles.Admin,
            "BulkList replaces the visibility rows of every user, so it must stay Admin-only");
    }

    [Test]
    public void BulkList_ShouldBeCoveredByTheControllerLevelAdminGate()
    {
        var methodInfo = typeof(GroupVisibilitiesController)
            .GetMethod("BulkList", BindingFlags.Public | BindingFlags.Instance);

        methodInfo.ShouldNotBeNull();

        var methodAttribute = methodInfo!.GetCustomAttribute<AuthorizeAttribute>(inherit: false);
        var effectiveRoles = methodAttribute?.Roles
            ?? typeof(GroupVisibilitiesController).GetCustomAttribute<AuthorizeAttribute>(inherit: false)?.Roles;

        effectiveRoles.ShouldBe(Roles.Admin,
            "Without a role gate any authenticated user could overwrite every user's group visibility");
    }
}
