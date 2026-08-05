// Copyright (c) Heribert Gasparoli Private. All rights reserved.

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
/// them to any authenticated user.
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
        yield return typeof(RecoveryController);
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
