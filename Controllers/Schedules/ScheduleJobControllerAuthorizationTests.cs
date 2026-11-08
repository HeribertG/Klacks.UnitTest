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
/// Every endpoint that starts or applies an engine run is pinned to JWT. The scheme has to be explicit: AddIdentity is
/// registered after AddJwtBearer and overrides the default to cookie auth, so a bare [Authorize] answers 401 to every
/// token client. The engine runs of the developer dropdown (Wizard 1, Harmonizer, Holistic Harmonizer) are admin-only;
/// the normal autofill (AutoWizardController) is planning work and open to every signed-in user (owner rule 2026-11-07),
/// pinned in AutofillControllerAuthorizationTests. A new job controller without its gates fails here rather than in production.
/// </summary>
[TestFixture]
public sealed class ScheduleJobControllerAuthorizationTests
{
    public static IEnumerable<Type> AdminScheduleJobControllers()
    {
        yield return typeof(WizardController);
        yield return typeof(HarmonizerController);
        yield return typeof(HolisticHarmonizerController);
    }

    public static IEnumerable<Type> ScheduleJobControllers()
    {
        foreach (var controller in AdminScheduleJobControllers())
        {
            yield return controller;
        }

        yield return typeof(AutoWizardController);
        // RecoveryController is not listed: it proposes a single-absence scenario instead of running an
        // engine job over the whole group and is open to every authenticated user (owner decision
        // 2026-10-06), see RecoveryControllerAuthorizationTests.
    }

    [TestCaseSource(nameof(AdminScheduleJobControllers))]
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
