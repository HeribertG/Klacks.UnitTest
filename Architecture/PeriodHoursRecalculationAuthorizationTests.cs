// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the manual period-hours recalculations (Works/RecalculatePeriodHours and Works/RecalculatePeriodHoursThorough) to
/// Admin (owner decision 2026-11-07). The JWT scheme comes from BaseController, so a signed-in non-admin gets 403, never
/// 401. The role check sits only on the controller: the automatic recalculations (work writes, the completion recovery)
/// call the services directly for every writer.
/// </summary>

using System.Reflection;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class PeriodHoursRecalculationAuthorizationTests
{
    [TestCase("RecalculatePeriodHours")]
    [TestCase("RecalculatePeriodHoursThorough")]
    public void ManualRecalculation_AdmitsOnlyAdmin(string route)
    {
        var action = PostAction(route);

        var roles = action.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Where(attribute => !string.IsNullOrWhiteSpace(attribute.Roles))
            .SelectMany(attribute => attribute.Roles!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();

        roles.ShouldBe([Roles.Admin]);
        action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).ShouldBeEmpty();
    }

    [Test]
    public void TheJwtScheme_IsPinnedByTheControllerBase()
    {
        var schemes = typeof(WorksController).BaseType!
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Select(attribute => attribute.AuthenticationSchemes);

        schemes.ShouldContain(JwtBearerDefaults.AuthenticationScheme);
    }

    private static MethodInfo PostAction(string route)
    {
        var action = typeof(WorksController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SingleOrDefault(method => method.GetCustomAttributes<HttpPostAttribute>().Any(post => post.Template == route));

        action.ShouldNotBeNull($"No POST action routed as '{route}' on {nameof(WorksController)}.");
        return action!;
    }
}
