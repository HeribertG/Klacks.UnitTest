// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Reflection;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Controllers.Schedules;

/// <summary>
/// Freezes the admin gate on every autofill entry point. These controllers start background jobs that
/// mutate the schedule and can consume LLM credits, so losing the role requirement would silently open
/// them to any authenticated user. RecoveryController is deliberately not in this list: cover-absence
/// proposes a scenario without a background job or LLM credits and is open to every authenticated user
/// (owner decision 2026-10-06), pinned in RecoveryControllerAuthorizationTests.
/// </summary>
[TestFixture]
public sealed class AutofillControllerAuthorizationTests
{
    private static IEnumerable<Type> AutofillControllers()
    {
        yield return typeof(WizardController);
        yield return typeof(HarmonizerController);
        yield return typeof(HolisticHarmonizerController);
        yield return typeof(AutoWizardController);
    }

    [TestCaseSource(nameof(AutofillControllers))]
    public void Controller_RequiresAdminRoleOnJwtScheme(Type controllerType)
    {
        var attributes = controllerType.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();

        attributes.ShouldNotBeEmpty($"{controllerType.Name} carries no [Authorize] attribute at all");
        attributes.ShouldContain(
            a => a.Roles == Roles.Admin
                 && a.AuthenticationSchemes == JwtBearerDefaults.AuthenticationScheme,
            $"{controllerType.Name} must pin Roles.Admin on the JWT bearer scheme");
    }
}
