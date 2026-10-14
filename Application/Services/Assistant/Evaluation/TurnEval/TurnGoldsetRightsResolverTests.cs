// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins how a goldset item's role becomes the rights list it is replayed with: no role keeps the run's own list
/// instance (bit-identical to the former behaviour), a role goes through Permissions.ExpandRoles, and a role
/// value outside the enum fails loudly instead of degrading to the Planer floor.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Constants;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

[TestFixture]
public class TurnGoldsetRightsResolverTests
{
    private static readonly List<string> RunRights = [Roles.Admin];

    [Test]
    public void Resolve_WithoutRole_ReturnsTheRunRightsInstanceUnchanged()
    {
        var rights = TurnGoldsetRightsResolver.Resolve(new TurnGoldsetItem(), RunRights);

        rights.ShouldBeSameAs(RunRights);
        rights.ShouldBe([Roles.Admin]);
    }

    [Test]
    public void Resolve_Admin_CarriesTheAdminRoleNameAndAllPermissions()
    {
        var rights = TurnGoldsetRightsResolver.Resolve(
            new TurnGoldsetItem { Role = TurnGoldsetRole.Admin }, []);

        rights.ShouldContain(Roles.Admin);
        rights.ShouldContain(Permissions.CanEditSettings);
        rights.ShouldContain(Permissions.CanDeleteClients);
    }

    [Test]
    public void Resolve_Supervisor_HoldsClientEditingButNeitherSettingsNorDeleting()
    {
        var rights = TurnGoldsetRightsResolver.Resolve(
            new TurnGoldsetItem { Role = TurnGoldsetRole.Supervisor }, RunRights);

        rights.ShouldContain(Roles.Authorised);
        rights.ShouldContain(Permissions.CanEditClients);
        rights.ShouldNotContain(Roles.Admin);
        rights.ShouldNotContain(Permissions.CanEditSettings);
        rights.ShouldNotContain(Permissions.CanDeleteClients);
    }

    [Test]
    public void Resolve_Planner_IsExactlyThePlannerFloorWithoutAnyRoleName()
    {
        var rights = TurnGoldsetRightsResolver.Resolve(
            new TurnGoldsetItem { Role = TurnGoldsetRole.Planner }, RunRights);

        rights.ShouldBe(Permissions.PlannerFloor.ToList());
        rights.ShouldNotContain(Roles.Admin);
        rights.ShouldNotContain(Roles.Authorised);
    }

    [TestCase("planner", TurnGoldsetRole.Planner)]
    [TestCase("supervisor", TurnGoldsetRole.Supervisor)]
    [TestCase("admin", TurnGoldsetRole.Admin)]
    public void Role_IsReadFromTheGoldsetJson(string value, TurnGoldsetRole expected)
    {
        var item = JsonSerializer.Deserialize<TurnGoldsetItem>($"{{\"role\": \"{value}\"}}", JsonOptions());

        item!.Role.ShouldBe(expected);
    }

    [Test]
    public void Role_IsNullWhenTheGoldsetJsonOmitsIt()
    {
        JsonSerializer.Deserialize<TurnGoldsetItem>("{}", JsonOptions())!.Role.ShouldBeNull();
    }

    [Test]
    public void Role_ThrowsOnAnUnknownValueInsteadOfFallingBackToThePlannerFloor()
    {
        Should.Throw<JsonException>(
            () => JsonSerializer.Deserialize<TurnGoldsetItem>("{\"role\": \"owner\"}", JsonOptions()));
    }

    private static JsonSerializerOptions JsonOptions() => new() { PropertyNameCaseInsensitive = true };
}
