// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.UnitTest.Infrastructure.Hubs;

/// <summary>
/// Evaluates the policy a hub's class [Authorize] attribute builds against a signed-in principal with the given roles,
/// through the real ASP.NET Core authorization service.
/// </summary>
internal static class HubAuthorizationEvaluator
{
    private const string UserName = "planner";
    private const string AuthenticationType = "Test";

    /// <summary>True when a principal with exactly these roles may connect to the hub.</summary>
    /// <param name="hubType">The hub class.</param>
    /// <param name="roles">Role claims of the principal; none = the planner floor.</param>
    public static async Task<bool> IsAuthorizedAsync(Type hubType, params string[] roles)
    {
        var services = new ServiceCollection().AddLogging().AddAuthorizationCore().BuildServiceProvider();
        var policyProvider = services.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await AuthorizationPolicy.CombineAsync(policyProvider, [hubType.GetCustomAttribute<AuthorizeAttribute>()!]);
        var claims = new List<Claim> { new(ClaimTypes.Name, UserName) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType));

        var result = await services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(principal, null, policy!);
        return result.Succeeded;
    }
}
