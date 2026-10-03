// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the approval surface of planning constraints (owner decision 2026-10-03: approval only from the UI
/// pending list). The controller must be Admin-only with the JWT scheme pinned, must not carry the
/// ICrudResourceController marker (SelfApiRouteResolver would otherwise offer its route to skills), its write
/// body must not carry server-controlled fields, and no skill source may reach the approve command.
/// </summary>

using System.Reflection;
using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Controllers.UserBackend.Scheduling;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class PlanningConstraintsControllerAuthorizationTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string SkillsDirectory = "Application/Skills";
    private const string ApproveCommandName = "ApprovePlanningConstraintCommand";
    private const string ApproveRouteFragment = "/approve";
    private const string ControllerRouteName = "PlanningConstraints";

    [Test]
    public void TheController_IsAdminOnly_AndPinsTheJwtScheme()
    {
        var attribute = typeof(PlanningConstraintsController).GetCustomAttribute<AuthorizeAttribute>(inherit: false);

        attribute.ShouldNotBeNull();
        attribute!.Roles.ShouldBe(Roles.Admin);
        attribute.AuthenticationSchemes.ShouldBe(JwtBearerDefaults.AuthenticationScheme);
    }

    [Test]
    public void NoActionWidensTheAccess()
    {
        var actions = typeof(PlanningConstraintsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        actions.ShouldNotBeEmpty();
        foreach (var action in actions)
        {
            action.GetCustomAttribute<AllowAnonymousAttribute>().ShouldBeNull($"{action.Name} must not allow anonymous access.");
        }
    }

    [Test]
    public void TheController_IsNotACrudResourceController()
    {
        typeof(PlanningConstraintsController).GetInterfaces()
            .ShouldNotContain(type => type.IsGenericType && type.Name.StartsWith("ICrudResourceController", StringComparison.Ordinal));
    }

    [Test]
    public void TheWriteBody_CarriesNoServerControlledField()
    {
        var properties = typeof(PlanningConstraintWriteResource).GetProperties().Select(p => p.Name).ToList();

        properties.ShouldNotContain("Id");
        properties.ShouldNotContain("Origin");
        properties.ShouldNotContain("ApprovalStatus");
        properties.ShouldNotContain("ApprovedBy");
        properties.ShouldNotContain("ApprovedAt");
        properties.ShouldNotContain("ProposedBy");
        properties.ShouldNotContain("PreviousVersionId");
    }

    [Test]
    public void NoSkill_ReachesTheApproval()
    {
        var skillsRoot = Path.Combine(LocateApiProject(), SkillsDirectory);
        var offenders = Directory.EnumerateFiles(skillsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                return text.Contains(ApproveCommandName, StringComparison.Ordinal)
                    || (text.Contains(ControllerRouteName, StringComparison.Ordinal)
                        && text.Contains(ApproveRouteFragment, StringComparison.Ordinal));
            })
            .ToList();

        offenders.ShouldBeEmpty("Approval of planning constraints is reserved to the UI pending list (owner decision 2026-10-03).");
    }

    private static string LocateApiProject()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"{ApiProjectDirectory} was not found above the test directory.");
    }
}
