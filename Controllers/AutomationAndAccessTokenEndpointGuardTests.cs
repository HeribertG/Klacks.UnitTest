// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the HTTP gate of the autonomy level, the agent plan writes and minting personal access tokens: these
/// endpoints require the same permissions as the Klacksy skills (CanManageAutomation, CanManageAccessTokens),
/// which lie outside the planner floor. Reading one's own plans stays open, because the chat lists them on
/// every open; listing and revoking one's own tokens stays open, so tokens issued before a downgrade can be cleaned up. The policy tests evaluate the registered policies for a planner (no role), a supervisor and
/// an admin.
/// </summary>

using System.Reflection;
using System.Security.Claims;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Controllers.Assistant;
using Klacks.Api.Presentation.Controllers.UserBackend.Authentification;
using Klacks.Api.Presentation.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class AutomationAndAccessTokenEndpointGuardTests
{
    private const string TestAuthenticationType = "Test";

    private static MethodInfo Action(Type controller, string method) =>
        controller.GetMethod(method, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"{controller.Name}.{method} not found");

    [Test]
    public void AgentAutonomyController_RequiresAutomationManagementWithThePinnedJwtScheme()
    {
        var authorize = typeof(AgentAutonomyController).GetCustomAttribute<AuthorizeAttribute>(inherit: false);

        authorize.ShouldNotBeNull("AgentAutonomyController must carry [Authorize]");
        authorize.Policy.ShouldBe(AuthorizationPolicies.RequireAutomationManagement);
        authorize.AuthenticationSchemes.ShouldBe(JwtBearerDefaults.AuthenticationScheme);
    }

    [Test]
    public void PersonalAccessTokenCreate_RequiresAccessTokenManagementWithThePinnedJwtScheme()
    {
        var authorize = Action(typeof(PersonalAccessTokensController), nameof(PersonalAccessTokensController.Create))
            .GetCustomAttribute<AuthorizeAttribute>(inherit: false);

        authorize.ShouldNotBeNull("PersonalAccessTokensController.Create must carry its own [Authorize]");
        authorize.Policy.ShouldBe(AuthorizationPolicies.RequireAccessTokenManagement);
        authorize.AuthenticationSchemes.ShouldBe(JwtBearerDefaults.AuthenticationScheme);
    }

    [TestCase(nameof(PersonalAccessTokensController.GetOwnTokens))]
    [TestCase(nameof(PersonalAccessTokensController.Revoke))]
    public void PersonalAccessTokenListAndRevoke_StayOpenForOwnTokens(string method)
    {
        Action(typeof(PersonalAccessTokensController), method).GetCustomAttribute<AuthorizeAttribute>(inherit: false)
            .ShouldBeNull($"PersonalAccessTokensController.{method} must stay open so tokens issued to an account before it lost the right can still be cleaned up");
    }

    [TestCase(nameof(AgentPlansController.CreateAndStartPlan))]
    [TestCase(nameof(AgentPlansController.ApproveAndContinue))]
    public void AgentPlanWrite_RequiresAutomationManagement(string method)
    {
        var authorize = Action(typeof(AgentPlansController), method).GetCustomAttribute<AuthorizeAttribute>(inherit: false);

        authorize.ShouldNotBeNull($"AgentPlansController.{method} must carry its own [Authorize]");
        authorize.Policy.ShouldBe(AuthorizationPolicies.RequireAutomationManagement);
        authorize.AuthenticationSchemes.ShouldBe(JwtBearerDefaults.AuthenticationScheme);
    }

    [TestCase(nameof(AgentPlansController.ListMyPlans))]
    [TestCase(nameof(AgentPlansController.GetPlan))]
    [TestCase(nameof(AgentPlansController.AbortPlan))]
    public void AgentPlanReadAndAbort_StayOpenToThePlannerFloor(string method)
    {
        Action(typeof(AgentPlansController), method).GetCustomAttribute<AuthorizeAttribute>(inherit: false)
            .ShouldBeNull($"AgentPlansController.{method} must stay open: the chat lists plans for every user, and aborting one's own running plan only lowers risk");
    }

    [TestCase(AuthorizationPolicies.RequireAutomationManagement, null, false)]
    [TestCase(AuthorizationPolicies.RequireAutomationManagement, Roles.Authorised, true)]
    [TestCase(AuthorizationPolicies.RequireAutomationManagement, Roles.Admin, true)]
    [TestCase(AuthorizationPolicies.RequireAccessTokenManagement, null, false)]
    [TestCase(AuthorizationPolicies.RequireAccessTokenManagement, Roles.Authorised, true)]
    [TestCase(AuthorizationPolicies.RequireAccessTokenManagement, Roles.Admin, true)]
    public async Task Policy_GrantsOnlyTheRolesHoldingThePermission(string policy, string? role, bool expected)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddAuthorization(options => options.AddKlacksPolicies())
            .BuildServiceProvider();
        var authorization = services.GetRequiredService<IAuthorizationService>();
        var claims = role == null ? Array.Empty<Claim>() : new[] { new Claim(ClaimTypes.Role, role) };
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, TestAuthenticationType));

        var result = await authorization.AuthorizeAsync(user, policy);

        result.Succeeded.ShouldBe(expected);
    }
}
