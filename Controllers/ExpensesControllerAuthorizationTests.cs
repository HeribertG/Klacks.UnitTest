// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the role gates of ExpensesController: recording, correcting and deleting an expense is open to the
/// planner floor (owner decision 2026-10-12, payroll export completeness), and the controller keeps serving
/// ExpensesResource for SelfApiRouteResolver.
/// </summary>

using System.Reflection;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Microsoft.AspNetCore.Authorization;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class ExpensesControllerAuthorizationTests
{
    [TestCase("Post")]
    [TestCase("Put")]
    [TestCase("Get")]
    [TestCase("GetList")]
    [TestCase("Delete")]
    public void EveryVerb_CarriesNoRoleRestriction(string action)
    {
        RoleGatesOf(action).ShouldBeEmpty(
            $"ExpensesController.{action} must stay open to every authenticated user; a role gate here " +
            "would keep a planner without a role from keeping the expenses of the day they plan correct.");
    }

    [Test]
    public void Controller_DoesNotInheritAGenericCrudBaseWhoseGateItCouldNotWiden()
    {
        typeof(ExpensesController).BaseType.ShouldBe(typeof(BaseController));
        typeof(ICrudResourceController<ExpensesResource>)
            .IsAssignableFrom(typeof(ExpensesController))
            .ShouldBeTrue("SelfApiRouteResolver maps ExpensesResource to its route through this marker.");
    }

    private static List<string> RoleGatesOf(string action)
    {
        var method = typeof(ExpensesController).GetMethod(action, BindingFlags.Public | BindingFlags.Instance);
        method.ShouldNotBeNull();

        var onAction = method!.GetCustomAttributes<AuthorizeAttribute>(inherit: true);
        var onType = typeof(ExpensesController).GetCustomAttributes<AuthorizeAttribute>(inherit: true);

        return onAction.Concat(onType)
            .Where(a => !string.IsNullOrWhiteSpace(a.Roles))
            .Select(a => a.Roles!)
            .ToList();
    }
}
