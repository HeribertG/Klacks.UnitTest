// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins POST RouteOptimization/geocode-all to Admin. The endpoint geocodes every address that lacks
/// coordinates through the shared Nominatim slot (max one request per second), so a single call runs for
/// a long time and reaches an external service; it is an operator procedure with no UI caller and no
/// matching granular right, which makes the Admin role the correct gate. Without the attribute the action
/// inherited only the class-level authentication from BaseController, so every signed-in caller,
/// including a planner without any role, could trigger it.
/// </summary>

using System.Reflection;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class GeocodeAllAuthorizationTests
{
    private const string GeocodeAllRoute = "geocode-all";

    [Test]
    public void GeocodeAll_AdmitsOnlyAdmin()
    {
        var roles = EffectiveRoles(GeocodeAllAction());

        roles.ShouldNotBeNull("geocode-all carries no role restriction, so any authenticated caller can start a bulk geocoding run.");
        roles!.ShouldBe(new[] { Roles.Admin }, ignoreOrder: true);
    }

    [Test]
    public void GeocodeAll_PinsTheJwtScheme()
    {
        var attributes = GeocodeAllAction().GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();

        attributes.ShouldNotBeEmpty();
        attributes.ShouldAllBe(a => a.AuthenticationSchemes == JwtBearerDefaults.AuthenticationScheme);
    }

    private static MethodInfo GeocodeAllAction()
    {
        var action = typeof(RouteOptimizationController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SingleOrDefault(m => m.GetCustomAttributes<HttpPostAttribute>().Any(a => a.Template == GeocodeAllRoute));

        action.ShouldNotBeNull($"No POST action routed as '{GeocodeAllRoute}' on {nameof(RouteOptimizationController)}.");
        return action!;
    }

    private static HashSet<string>? EffectiveRoles(MethodInfo action)
    {
        var attributes = new List<AuthorizeAttribute>();
        attributes.AddRange(action.GetCustomAttributes<AuthorizeAttribute>(inherit: true));

        for (var current = action.DeclaringType; current is not null && current != typeof(object); current = current.BaseType)
        {
            attributes.AddRange(current.GetCustomAttributes<AuthorizeAttribute>(inherit: false));
        }

        HashSet<string>? allowed = null;
        foreach (var attribute in attributes.Where(a => !string.IsNullOrWhiteSpace(a.Roles)))
        {
            var roles = attribute.Roles!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);

            if (allowed is null)
            {
                allowed = roles;
                continue;
            }

            allowed.IntersectWith(roles);
        }

        return allowed;
    }
}
