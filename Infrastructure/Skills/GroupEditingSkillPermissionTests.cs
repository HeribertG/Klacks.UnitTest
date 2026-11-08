// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the seeded rights of the group-editing skills to the same rule the rest of the stack applies:
/// the group structure is a setting and Admin-only (owner decision 2026-11-07, roles are work areas). The
/// REST endpoints they write through (Groups PUT, POST Groups/move/{id} and PUT Groups/{id}/location) are
/// Admin-only, the Angular group forms and tree drag-and-drop ask for CanEditGroups, and the skills ask for
/// the same right, which the Authorised role no longer expands to. Deliberately not covered: delete_group
/// (GroupCreationSkillPermissionTests) and list_groups_hierarchical.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class GroupEditingSkillPermissionTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string UpdateGroupSkill = "update_group";
    private const string MoveGroupSkill = "move_group";
    private const string SetGroupLocationSkill = "set_group_location";

    private const string VersionBumpReminder =
        "Remember the version bump when changing this - SkillSeedLoader skips a seed whose version did " +
        "not increase, so the change never reaches an existing database.";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    [TestCase(UpdateGroupSkill)]
    [TestCase(MoveGroupSkill)]
    [TestCase(SetGroupLocationSkill)]
    public void GroupEditingSkill_AsksForTheGroupEditingRight_NotForSettings(string skillName)
    {
        var permissions = SeededPermissionsOf(skillName);

        permissions.ShouldContain(
            Permissions.CanEditGroups,
            $"'{skillName}' edits a group and asks for the group-editing right. " +
            VersionBumpReminder);

        permissions.ShouldNotContain(
            Permissions.CanEditSettings,
            $"'{skillName}' is gated on the group right, not on CanEditSettings, so the seed and the Ui " +
            $"read the same right. {VersionBumpReminder}");
    }

    [TestCase(UpdateGroupSkill)]
    [TestCase(MoveGroupSkill)]
    [TestCase(SetGroupLocationSkill)]
    public void GroupEditingSkill_IsNotReachableForASupervisor(string skillName)
    {
        var supervisorRights = Permissions.ExpandRoles(new[] { Roles.Authorised });

        SeededPermissionsOf(skillName).All(supervisorRights.Contains).ShouldBeFalse(
            $"'{skillName}' changes the group structure, which is a setting and Admin-only " +
            "(owner decision 2026-11-07).");
    }

    [TestCase(UpdateGroupSkill)]
    [TestCase(MoveGroupSkill)]
    [TestCase(SetGroupLocationSkill)]
    public void GroupEditingSkill_IsNotReachableFromThePlannerFloor(string skillName)
    {
        var floorRights = Permissions.ExpandRoles(Array.Empty<string>());

        SeededPermissionsOf(skillName).All(floorRights.Contains).ShouldBeFalse(
            $"'{skillName}' would be reachable by every authenticated caller: the Planer floor " +
            "already covers every right it asks for. Changing a group needs a role.");
    }

    private static List<string> SeededPermissionsOf(string skillName)
    {
        var json = File.ReadAllText(LocateDefinitionsFile(SkillSeedsFileName));
        using var document = JsonDocument.Parse(json);

        var skill = document.RootElement.GetProperty("skills")
            .EnumerateArray()
            .FirstOrDefault(s => s.TryGetProperty("name", out var n) && n.GetString() == skillName);

        skill.ValueKind.ShouldNotBe(JsonValueKind.Undefined, $"'{skillName}' is missing from the seed file.");

        return skill.GetProperty("requiredPermissions")
            .EnumerateArray()
            .Select(p => p.GetString() ?? string.Empty)
            .ToList();
    }

    private static string LocateDefinitionsFile(string fileName)
    {
        return RepositoryRootLocator.RequireFile([.. DefinitionsRelativePath, fileName]);
    }
}
