// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Owner decision 2026-10-05: writing calendar selections and their selected calendars is Admin-only, like
/// CalendarRulesController - a selection decides which holidays are official for every contract that uses it,
/// so it drives holiday-work warnings and holiday surcharges. Reads stay open.
///
/// MVC reads an action's [Authorize] attributes with inherit: true and AND-combines them, so the effective rule is:
/// a role passes only if EVERY role-carrying attribute admits it. The tests evaluate exactly that.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Controllers.UserBackend.CalendarSelections;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using System.Reflection;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class CalendarSelectionWriteAuthorizationTests
{
    private const char RoleSeparator = ',';
    private const string NoRole = "";

    private static readonly Type[] Controllers = [typeof(CalendarSelectionsController), typeof(SelectedCalendarsController)];

    private static IEnumerable<TestCaseData> WriteVerbs() =>
        from controller in Controllers
        from verb in new[] { "Post", "Put", "Delete" }
        select new TestCaseData(controller, verb).SetName($"{controller.Name}.{verb}");

    private static IEnumerable<TestCaseData> ReadVerbs() =>
        new[]
        {
            new TestCaseData(typeof(CalendarSelectionsController), "Get"),
            new TestCaseData(typeof(CalendarSelectionsController), "GetCalendarSelections"),
            new TestCaseData(typeof(CalendarSelectionsController), "GetUsedByContracts"),
            new TestCaseData(typeof(SelectedCalendarsController), "Get"),
        }.Select(c => c.SetName($"{((Type)c.Arguments[0]!).Name}.{c.Arguments[1]}"));

    [TestCaseSource(nameof(WriteVerbs))]
    public void WriteVerb_RejectsTheSupervisorRole(Type controller, string verb)
    {
        IsAllowed(controller, verb, Roles.Authorised).ShouldBeFalse();
        IsAllowed(controller, verb, NoRole).ShouldBeFalse();
    }

    [TestCaseSource(nameof(WriteVerbs))]
    public void WriteVerb_AdmitsTheAdmin(Type controller, string verb)
    {
        IsAllowed(controller, verb, Roles.Admin).ShouldBeTrue();
    }

    [TestCaseSource(nameof(WriteVerbs))]
    public void WriteVerb_PinsTheJwtSchemeOnItsOwnAttribute(Type controller, string verb)
    {
        var own = Method(controller, verb).GetCustomAttributes<AuthorizeAttribute>(inherit: false).ToList();

        own.ShouldNotBeEmpty($"{controller.Name}.{verb} must declare the Admin restriction itself.");
        own.ShouldAllBe(a => a.AuthenticationSchemes == JwtBearerDefaults.AuthenticationScheme);
    }

    [TestCaseSource(nameof(ReadVerbs))]
    public void ReadVerb_StaysOpenToEveryAuthenticatedCaller(Type controller, string verb)
    {
        IsAllowed(controller, verb, Roles.Authorised).ShouldBeTrue();
        IsAllowed(controller, verb, NoRole).ShouldBeTrue();
    }

    private static bool IsAllowed(Type controller, string verb, string role)
    {
        var attributes = Method(controller, verb).GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Concat(controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true));

        return attributes
            .Where(a => !string.IsNullOrEmpty(a.Roles))
            .All(a => a.Roles!.Split(RoleSeparator).Select(r => r.Trim()).Contains(role));
    }

    private static MethodInfo Method(Type controller, string verb)
    {
        var method = controller.GetMethod(verb, BindingFlags.Public | BindingFlags.Instance);
        method.ShouldNotBeNull($"{controller.Name}.{verb} was not found - the test is stale.");
        return method!;
    }
}
