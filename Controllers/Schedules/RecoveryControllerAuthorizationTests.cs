// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Reflection;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Controllers.Schedules;

/// <summary>
/// Pins the gate of the cover-absence front door: every authenticated user on the JWT scheme, no role
/// restriction (owner decision 2026-10-06, docs/ENTWURF-ausfall-ersatz-planer-2026-10-06.md, E1). The flow
/// only proposes a scenario, the planner floor may edit the schedule anyway, accepting a scenario is open to
/// every authenticated user, the compliance override stays with ISupervisorOverrideAuthorizer and the
/// candidate pool is filtered by the caller's group visibility. Narrowing it back to a role is a decision,
/// so it fails here instead of slipping in; dropping the scheme would route token clients to cookie auth.
/// </summary>
[TestFixture]
public sealed class RecoveryControllerAuthorizationTests
{
    [Test]
    public void Controller_IsOpenToEveryAuthenticatedUser_OnTheJwtScheme()
    {
        var attributes = typeof(RecoveryController).GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();

        attributes.ShouldNotBeEmpty("RecoveryController must carry an [Authorize] attribute");
        attributes.ShouldAllBe(a => a.Roles == null && a.Policy == null);
        attributes.ShouldContain(
            a => a.AuthenticationSchemes == JwtBearerDefaults.AuthenticationScheme,
            "RecoveryController must pin the JWT bearer scheme");
    }

    [Test]
    public void Candidates_IsAReadOnlyGetEndpoint()
    {
        var method = typeof(RecoveryController).GetMethod(nameof(RecoveryController.Candidates));

        method.ShouldNotBeNull();
        method!.GetCustomAttribute<HttpGetAttribute>().ShouldNotBeNull();
    }
}
