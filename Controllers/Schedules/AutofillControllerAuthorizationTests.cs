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
/// Freezes the admin gate on the autofill entry points of the developer dropdown (Ctrl+Shift+H): Wizard 1, Harmonizer and
/// Holistic Harmonizer stay Admin-only (owner decision 2026-11-07). The normal autofill (AutoWizardController) is planning
/// work (owner rule 2026-11-07, "Rollen = Arbeitsbereiche"): it needs the planning right CanEditSchedule through the policy
/// RequireScheduleEditing and no role; its second test pins exactly that, so a role cannot creep back in unnoticed. RecoveryController is deliberately not in this list: cover-absence
/// is open to every authenticated user (owner decision 2026-10-06), pinned in RecoveryControllerAuthorizationTests.
/// </summary>
[TestFixture]
public sealed class AutofillControllerAuthorizationTests
{
    private static IEnumerable<Type> AutofillControllers()
    {
        yield return typeof(WizardController);
        yield return typeof(HarmonizerController);
        yield return typeof(HolisticHarmonizerController);
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

    [Test]
    public void AutoWizardController_RequiresThePlanningRightOnTheJwtScheme()
    {
        var attribute = typeof(AutoWizardController).GetCustomAttributes<AuthorizeAttribute>(inherit: false).Single();

        attribute.AuthenticationSchemes.ShouldBe(JwtBearerDefaults.AuthenticationScheme);
        attribute.Roles.ShouldBeNull("autofill is planning work, not an admin task (owner rule 2026-11-07)");
        attribute.Policy.ShouldBe(AuthorizationPolicies.RequireScheduleEditing);
        Permissions.PlannerFloor.ShouldContain(Permissions.CanEditSchedule);
    }
}
