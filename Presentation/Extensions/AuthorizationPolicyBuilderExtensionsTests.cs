// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the assistant gate itself: the assertion behind RequireAssistant lets every current
/// role through (the owner decision was to keep the assistant for User too) and rejects a caller whose
/// token carries no role at all — that caller would otherwise reach every skill that has no required
/// permission set.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;

namespace Klacks.UnitTest.Presentation.Extensions;

[TestFixture]
public class AuthorizationPolicyBuilderExtensionsTests
{
    private static async Task<bool> IsAllowedAsync(ClaimsPrincipal user)
    {
        var policy = new AuthorizationPolicyBuilder().RequireAssistantAccess().Build();
        var context = new AuthorizationHandlerContext(policy.Requirements, user, null);

        foreach (var requirement in policy.Requirements.OfType<AssertionRequirement>())
        {
            await requirement.HandleAsync(context);
        }

        return context.HasSucceeded;
    }

    private static ClaimsPrincipal WithRoles(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(r => new Claim(ClaimTypes.Role, r)), "test"));

    [TestCase(Roles.Admin)]
    [TestCase(Roles.Authorised)]
    [TestCase(Roles.User)]
    public async Task RequireAssistantAccess_EveryRole_IsAllowed(string role)
    {
        (await IsAllowedAsync(WithRoles(role))).ShouldBeTrue();
    }

    [Test]
    public async Task RequireAssistantAccess_NoRoleClaim_IsRejected()
    {
        (await IsAllowedAsync(new ClaimsPrincipal(new ClaimsIdentity()))).ShouldBeFalse();
    }
}
