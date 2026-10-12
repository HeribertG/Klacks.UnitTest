// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the role gates of the schedule-day entry controllers (Expenses, ScheduleNotes, ScheduleCommands):
/// recording, correcting and deleting them is open to the planner floor (owner decisions 2026-10-12), and each
/// controller keeps serving its resource for SelfApiRouteResolver.
/// </summary>

using System.Reflection;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Microsoft.AspNetCore.Authorization;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class PlannerFloorScheduleEntryControllerAuthorizationTests
{
    private static readonly string[] Verbs = ["Post", "Put", "Get", "GetList", "Delete"];

    private static readonly (Type Controller, Type Resource)[] Controllers =
    [
        (typeof(ExpensesController), typeof(ExpensesResource)),
        (typeof(ScheduleNotesController), typeof(ScheduleNoteResource)),
        (typeof(ScheduleCommandsController), typeof(ScheduleCommandResource))
    ];

    private static IEnumerable<TestCaseData> ControllerVerbs() =>
        Controllers.SelectMany(c => Verbs.Select(verb =>
            new TestCaseData(c.Controller, verb).SetName($"{c.Controller.Name}_{verb}_CarriesNoRoleRestriction")));

    private static IEnumerable<TestCaseData> ControllerResources() =>
        Controllers.Select(c =>
            new TestCaseData(c.Controller, c.Resource).SetName($"{c.Controller.Name}_ServesItsResourceFromBaseController"));

    [TestCaseSource(nameof(ControllerVerbs))]
    public void EveryVerb_CarriesNoRoleRestriction(Type controller, string action)
    {
        RoleGatesOf(controller, action).ShouldBeEmpty(
            $"{controller.Name}.{action} must stay open to every authenticated user; a role gate here would keep " +
            "a planner without a role from keeping the day they plan complete.");
    }

    [TestCaseSource(nameof(ControllerResources))]
    public void Controller_ServesItsResourceFromBaseController(Type controller, Type resource)
    {
        controller.BaseType.ShouldBe(
            typeof(BaseController),
            "A generic CRUD base would bring a role gate on Post/Put/Delete that an override could never widen.");
        typeof(ICrudResourceController<>).MakeGenericType(resource)
            .IsAssignableFrom(controller)
            .ShouldBeTrue("SelfApiRouteResolver maps the resource to its route through this marker.");
    }

    private static List<string> RoleGatesOf(Type controller, string action)
    {
        var method = controller.GetMethod(action, BindingFlags.Public | BindingFlags.Instance);
        method.ShouldNotBeNull();

        var onAction = method!.GetCustomAttributes<AuthorizeAttribute>(inherit: true);
        var onType = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true);

        return onAction.Concat(onType)
            .Where(a => !string.IsNullOrWhiteSpace(a.Roles))
            .Select(a => a.Roles!)
            .ToList();
    }
}
