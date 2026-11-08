// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the owner decision of 2026-11-07, roles are work areas, on the generic CRUD writes it re-gated: contract
/// templates, the group structure and the scheduling rule families are settings, so the Post and Put of EVERY generic
/// CRUD controller serving one of these resources are Admin-only, while the reads of the main controllers stay open to
/// every authenticated caller. The controllers are discovered by reflection (ICrudResourceController of a settings
/// resource), because a second controller on the same resource (AssignedGroupsController next to GroupsController)
/// would otherwise reopen the write unnoticed. The owner decision of 2026-11-08 (all settings are Admin work) added
/// the absence types with their extended absences, the monthly target hours, the individual periods, the countries
/// and the states. The overrides only add [Authorize(Roles = Admin)] to the inherited
/// Admin/Authorised attribute; both are AND-combined (AuthorizeAttributeInheritanceTests), so the roles an endpoint
/// admits are the intersection of every role-bearing attribute on the action and on its type chain.
/// </summary>

using System.Reflection;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.DTOs.Settings;
using Klacks.Api.Presentation.Controllers.UserBackend.Associations;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Klacks.Api.Presentation.Controllers.UserBackend.Scheduling;
using Klacks.Api.Presentation.Controllers.UserBackend.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class WorkAreaEndpointAuthorizationTests
{
    private const string PostAction = "Post";
    private const string PutAction = "Put";
    private const string DeleteAction = "Delete";
    private const string GetAction = "Get";
    private const int MinimumSettingsControllers = 14;

    private static readonly Type[] SettingsResources =
    [
        typeof(ContractResource),
        typeof(GroupResource),
        typeof(SchedulingRuleResource),
        typeof(CounterRuleResource),
        typeof(PeriodCapRuleResource),
        typeof(RestrictedTimeWindowRuleResource),
        typeof(AbsenceResource),
        typeof(AbsenceDetailResource),
        typeof(MonthlyTargetHoursResource),
        typeof(IndividualPeriodResource),
        typeof(CountryResource),
        typeof(StateResource)
    ];

    private static readonly Type[] SettingsReadControllers =
    [
        typeof(ContractsController),
        typeof(GroupsController),
        typeof(SchedulingRulesController),
        typeof(CounterRulesController),
        typeof(PeriodCapRulesController),
        typeof(RestrictedTimeWindowRulesController),
        typeof(AbsencesController),
        typeof(AbsenceDetailsController),
        typeof(MonthlyTargetHoursController),
        typeof(IndividualPeriodsController),
        typeof(CountriesController),
        typeof(StatesController)
    ];

    private static IEnumerable<TestCaseData> SettingsWrites() =>
        SettingsControllers().SelectMany(controller => new[]
        {
            new TestCaseData(controller, PostAction).SetName($"{controller.Name}_{PostAction}_IsAdminOnly"),
            new TestCaseData(controller, PutAction).SetName($"{controller.Name}_{PutAction}_IsAdminOnly"),
            new TestCaseData(controller, DeleteAction).SetName($"{controller.Name}_{DeleteAction}_IsAdminOnly")
        });

    private static IEnumerable<TestCaseData> SettingsReads() =>
        SettingsReadControllers.Select(controller =>
            new TestCaseData(controller).SetName($"{controller.Name}_{GetAction}_StaysOpen"));

    [Test]
    public void TheDiscovery_FindsEveryKnownSettingsController()
    {
        var discovered = SettingsControllers().ToList();

        discovered.Count.ShouldBeGreaterThanOrEqualTo(
            MinimumSettingsControllers,
            "The scan found fewer controllers than are known to serve a settings resource; an empty or short list proves nothing.");
        discovered.ShouldContain(typeof(GroupsController));
        discovered.ShouldContain(typeof(ContractsController));
        discovered.ShouldContain(typeof(AbsencesController));
        discovered.ShouldContain(typeof(StatesController));
    }

    [TestCaseSource(nameof(SettingsWrites))]
    public void SettingsWrite_AdmitsOnlyAdmin(Type controller, string actionName)
    {
        var admitted = EffectiveRoles(controller, ActionOf(controller, actionName));

        admitted.ShouldNotBeNull($"{controller.Name}.{actionName} carries no role restriction at all.");
        admitted!.ShouldBe(
            new[] { Roles.Admin },
            ignoreOrder: true,
            $"{controller.Name}.{actionName} writes a setting; under the work-area rule only the Admin may do that.");
    }

    [TestCaseSource(nameof(SettingsReads))]
    public void SettingsRead_StaysOpenToEveryAuthenticatedCaller(Type controller)
    {
        var read = controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(method => method.Name == GetAction
                && method.GetParameters() is [{ ParameterType: var type }] && type == typeof(Guid));

        EffectiveRoles(controller, read).ShouldBeNull(
            $"{controller.Name}.{GetAction} must stay readable for the Planer and the Supervisor: the employee " +
            "card lists the contracts, every page reads the group tree, the schedule reads the absence types and " +
            "addresses read the countries and states.");
    }

    private static IEnumerable<Type> SettingsControllers() =>
        typeof(GroupsController).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(type))
            .Where(type => type.GetInterfaces().Any(contract =>
                contract.IsGenericType
                && contract.GetGenericTypeDefinition() == typeof(ICrudResourceController<>)
                && SettingsResources.Contains(contract.GetGenericArguments()[0])))
            .OrderBy(type => type.Name, StringComparer.Ordinal);

    private static MethodInfo ActionOf(Type controller, string actionName) =>
        controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(method => method.Name == actionName && method.GetParameters().Length == 1);

    /// <summary>
    /// The roles a caller can hold and still reach the action, or null when no attribute restricts roles. Every
    /// role-bearing attribute has to be satisfied on its own, so the admitted set is the intersection.
    /// </summary>
    /// <param name="controller">The concrete controller</param>
    /// <param name="action">The public action whose endpoint is inspected</param>
    private static HashSet<string>? EffectiveRoles(Type controller, MethodInfo action)
    {
        var attributes = action.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
        for (var current = controller; current is not null && current != typeof(object); current = current.BaseType)
        {
            attributes.AddRange(current.GetCustomAttributes<AuthorizeAttribute>(inherit: false));
        }

        HashSet<string>? admitted = null;
        foreach (var attribute in attributes.Where(a => !string.IsNullOrWhiteSpace(a.Roles)))
        {
            var roles = attribute.Roles!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);

            if (admitted is null)
            {
                admitted = roles;
                continue;
            }

            admitted.IntersectWith(roles);
        }

        return admitted;
    }
}
