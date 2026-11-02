// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the access of the variant (key) endpoints: reading the list and the shifts of a key is open to every
/// authenticated user, every other action of the controller (including the delete of a single key shift, which the REST
/// shift delete keeps admin-only) needs Admin or Authorised, no action allows anonymous
/// access, and the JWT scheme stays pinned through BaseController. A new action therefore starts protected and has to
/// be added to the read list on purpose to be opened.
/// </summary>

using System.Reflection;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ScenarioVariantsAuthorizationTests
{
    private static readonly string WriteRoles = $"{Roles.Admin},{Roles.Authorised}";

    private static readonly string[] ReadActions =
    {
        nameof(ScenarioVariantsController.GetList),
        nameof(ScenarioVariantsController.GetShifts)
    };

    private static readonly string[] WriteActions =
    {
        nameof(ScenarioVariantsController.Create),
        nameof(ScenarioVariantsController.Update),
        nameof(ScenarioVariantsController.Delete),
        nameof(ScenarioVariantsController.DeleteShift),
        nameof(ScenarioVariantsController.CopyShifts)
    };

    private static IEnumerable<MethodInfo> Actions()
    {
        return typeof(ScenarioVariantsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
    }

    [Test]
    public void TheController_PinsTheJwtSchemeThroughItsBaseController()
    {
        var attributes = typeof(ScenarioVariantsController).GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();

        attributes.ShouldContain(a => a.AuthenticationSchemes == JwtBearerDefaults.AuthenticationScheme);
    }

    [TestCaseSource(nameof(WriteActions))]
    public void WriteActions_RequireAdminOrAuthorised(string actionName)
    {
        var action = Actions().Single(m => m.Name == actionName);

        var attribute = action.GetCustomAttribute<AuthorizeAttribute>(inherit: false);

        attribute.ShouldNotBeNull($"{actionName} must carry its own [Authorize]");
        attribute!.Roles.ShouldBe(WriteRoles);
    }

    [TestCaseSource(nameof(ReadActions))]
    public void ReadActions_AddNoRoleRequirement(string actionName)
    {
        var action = Actions().Single(m => m.Name == actionName);

        action.GetCustomAttribute<AuthorizeAttribute>(inherit: false).ShouldBeNull($"{actionName} is open to every authenticated user");
    }

    [Test]
    public void DeleteShift_IsOpenToSupervisorsThroughTheVariantRouteAndNotThroughTheShiftDelete()
    {
        var action = Actions().Single(m => m.Name == nameof(ScenarioVariantsController.DeleteShift));

        action.GetCustomAttribute<HttpDeleteAttribute>().ShouldNotBeNull().Template.ShouldBe("{id}/Shifts/{shiftId}");
        action.GetCustomAttribute<AuthorizeAttribute>(inherit: false)!.Roles.ShouldContain(Roles.Authorised);
        typeof(ShiftsController).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.Name == nameof(ShiftsController.Delete))
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true).Single().Roles.ShouldBe(Roles.Admin);
    }

    [Test]
    public void EveryOtherAction_IsAWriteActionAndRequiresAdminOrAuthorised()
    {
        var others = Actions().Where(m => !ReadActions.Contains(m.Name)).ToList();

        others.Select(m => m.Name).ShouldBe(WriteActions, ignoreOrder: true);
        foreach (var action in others)
        {
            var attribute = action.GetCustomAttribute<AuthorizeAttribute>(inherit: false);
            attribute.ShouldNotBeNull($"{action.Name} must carry its own [Authorize]");
            attribute!.Roles.ShouldBe(WriteRoles, $"{action.Name} must require Admin or Authorised");
        }
    }

    [Test]
    public void NoActionAllowsAnonymousAccess()
    {
        var actions = Actions().ToList();

        actions.ShouldNotBeEmpty();
        foreach (var action in actions)
        {
            action.GetCustomAttribute<AllowAnonymousAttribute>().ShouldBeNull($"{action.Name} must not allow anonymous access");
        }

        typeof(ScenarioVariantsController).GetCustomAttribute<AllowAnonymousAttribute>().ShouldBeNull();
    }
}
