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
/// Every endpoint that starts or applies an engine run rewrites the schedule of a whole group, so all of
/// them are admin-only and pinned to JWT. The scheme has to be explicit: AddIdentity is registered after
/// AddJwtBearer and overrides the default to cookie auth, so a bare [Authorize] answers 401 to every
/// token client. A new job controller without both gates fails here rather than in production.
/// </summary>
[TestFixture]
public sealed class ScheduleJobControllerAuthorizationTests
{
    public static IEnumerable<Type> ScheduleJobControllers()
    {
        yield return typeof(WizardController);
        yield return typeof(HarmonizerController);
        yield return typeof(HolisticHarmonizerController);
        yield return typeof(AutoWizardController);
        yield return typeof(RecoveryController);
    }

    [TestCaseSource(nameof(ScheduleJobControllers))]
    public void Controller_IsAdminOnly(Type controllerType)
    {
        var attributes = controllerType.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();

        attributes.ShouldNotBeEmpty($"{controllerType.Name} must carry an [Authorize] attribute.");
        attributes.ShouldContain(
            a => a.Roles != null && a.Roles.Contains(Roles.Admin, StringComparison.Ordinal),
            $"{controllerType.Name} starts or applies engine runs and must be restricted to {Roles.Admin}.");
    }

    [TestCaseSource(nameof(ScheduleJobControllers))]
    public void Controller_PinsTheJwtScheme(Type controllerType)
    {
        var attributes = controllerType.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();

        attributes.ShouldContain(
            a => a.AuthenticationSchemes == JwtBearerDefaults.AuthenticationScheme,
            $"{controllerType.Name} must pin JwtBearerDefaults.AuthenticationScheme — without it the "
            + "runtime default applies, which AddIdentity overrides to cookie auth.");
    }
}
