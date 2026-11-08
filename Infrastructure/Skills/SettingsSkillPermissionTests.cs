// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the Klacksy write skills of the settings the owner decision of 2026-11-08 made Admin-only (all settings are
/// Admin work): absence types, monthly target hours and individual periods. Their HTTP writes are Admin-only through
/// the Post/Put overrides (WorkAreaEndpointAuthorizationTests); the skills have to agree, otherwise a supervisor could
/// reach through Klacksy what the endpoint refuses. The seed gate is CanEditSettings, which only the Admin role
/// expands to. Countries, states and the extended absences have no write skills (list_countries and list_states are
/// reads).
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SettingsSkillPermissionTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] SettingsWriteSkills =
    [
        "create_absence_type",
        "update_absence_type",
        "delete_absence_type",
        "create_absence",
        "update_absence",
        "delete_absence",
        "assign_macro_to_absence_type",
        "create_monthly_target_hours",
        "update_monthly_target_hours",
        "delete_monthly_target_hours",
        "create_individual_period",
        "update_individual_period",
        "delete_individual_period"
    ];

    [TestCaseSource(nameof(SettingsWriteSkills))]
    public void SettingsWriteSkill_RequiresTheSettingsEditingRight(string skillName)
    {
        PermissionsOf(skillName).ShouldContain(
            Permissions.CanEditSettings,
            $"'{skillName}' writes a setting, which is Admin work since the owner decision of 2026-11-08.");
    }

    [TestCaseSource(nameof(SettingsWriteSkills))]
    public void SettingsWriteSkill_IsNotHeldByASupervisorOrThePlannerFloor(string skillName)
    {
        var permissions = PermissionsOf(skillName);

        permissions.All(Permissions.ExpandRoles([Roles.Authorised]).Contains).ShouldBeFalse(
            $"'{skillName}' would be reachable by a supervisor, but settings are Admin work.");
        permissions.All(Permissions.PlannerFloor.Contains).ShouldBeFalse(
            $"'{skillName}' would be reachable by a caller without a role.");
    }

    private static List<string> PermissionsOf(string skillName)
    {
        var json = File.ReadAllText(RepositoryRootLocator.RequireFile([.. DefinitionsRelativePath, SkillSeedsFileName]));
        using var document = JsonDocument.Parse(json);

        var skill = document.RootElement.GetProperty("skills")
            .EnumerateArray()
            .FirstOrDefault(s => s.TryGetProperty("name", out var n) && n.GetString() == skillName);

        skill.ValueKind.ShouldNotBe(JsonValueKind.Undefined, $"'{skillName}' is missing from the seed file.");

        return skill.GetProperty("requiredPermissions")
            .EnumerateArray().Select(p => p.GetString() ?? string.Empty).ToList();
    }
}
