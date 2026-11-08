// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the seeded rights of the group-editing skills to the same rule the rest of the stack applies:
/// the group structure is a setting and Admin-only (owner decision 2026-11-07, roles are work areas). The
/// REST endpoints move_group and set_group_location write through (POST Groups/move/{id}, PUT
/// Groups/{id}/location) are Admin-only, the Angular tree drag-and-drop asks for CanEditGroups, and the skills
/// ask for the same right, which the Authorised role does not expand to. update_group is the exception (owner
/// decision 2026-11-08): it asks for CanEditSubgroups like the Angular group form, and the Groups Put handler
/// lets a caller without the Admin role change only name, description, validity and members of a subgroup.
/// Deliberately not covered: delete_group (GroupCreationSkillPermissionTests) and list_groups_hierarchical.
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

    [TestCase(MoveGroupSkill)]
    [TestCase(SetGroupLocationSkill)]
    public void GroupEditingSkill_IsNotReachableForASupervisor(string skillName)
    {
        var supervisorRights = Permissions.ExpandRoles(new[] { Roles.Authorised });

        SeededPermissionsOf(skillName).All(supervisorRights.Contains).ShouldBeFalse(
            $"'{skillName}' changes the group structure, which is a setting and Admin-only " +
            "(owner decision 2026-11-07).");
    }

    [TestCase(MoveGroupSkill)]
    [TestCase(SetGroupLocationSkill)]
    public void GroupEditingSkill_IsNotReachableFromThePlannerFloor(string skillName)
    {
        var floorRights = Permissions.ExpandRoles(Array.Empty<string>());

        SeededPermissionsOf(skillName).All(floorRights.Contains).ShouldBeFalse(
            $"'{skillName}' would be reachable by every authenticated caller: the Planer floor " +
            "already covers every right it asks for. Changing a group needs a role.");
    }

    [Test]
    public void UpdateGroupSkill_AsksForTheSubgroupRight_AndIsBoundToAnIdentifiedCaller()
    {
        SeededPermissionsOf(UpdateGroupSkill).ShouldBe(
            new List<string> { Permissions.CanEditSubgroups },
            $"update_group is gated on CanEditSubgroups (owner decision 2026-11-08). {VersionBumpReminder}");

        Permissions.ExpandRoles(Array.Empty<string>()).ShouldNotContain(Permissions.CanEditSubgroups);
        GroupVisibilityBoundSkills.Contains(UpdateGroupSkill).ShouldBeTrue(
            "A supervisor's update_group decides through the group visibility guard, which treats a missing user as " +
            "unrestricted; the executor must refuse a non-admin call without an identified user.");
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
