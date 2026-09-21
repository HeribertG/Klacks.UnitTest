// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the seeded rights of the group-editing skills to the same rule the rest of the stack applies:
/// the REST endpoints they write through (InputBaseController.Put, POST Groups/move/{id} and
/// PUT Groups/{id}/location, all Admin plus Authorised) and the Angular group forms and tree
/// drag-and-drop (CanEditGroups) accept a Supervisor, while the seed demanded CanEditSettings, which
/// only the Admin holds. A Supervisor could therefore create a group after the owner decision of
/// 2026-09-21 and change, move or locate it in the Ui, but not do any of that through the assistant.
/// Deliberately not covered: delete_group and list_groups_hierarchical, which the same owner decision
/// left on the settings rights.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;

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
            $"'{skillName}' edits a group, which an Admin and a Supervisor may both do. " +
            VersionBumpReminder);

        permissions.ShouldNotContain(
            Permissions.CanEditSettings,
            $"'{skillName}' must not be gated on CanEditSettings - only the Admin holds it, so a " +
            $"Supervisor would be refused a change the Ui and the endpoint both allow. {VersionBumpReminder}");
    }

    [TestCase(UpdateGroupSkill)]
    [TestCase(MoveGroupSkill)]
    [TestCase(SetGroupLocationSkill)]
    public void GroupEditingSkill_EveryRightItAsksFor_IsHeldByASupervisor(string skillName)
    {
        var supervisorRights = Permissions.ExpandRoles(new[] { Roles.Authorised });

        foreach (var permission in SeededPermissionsOf(skillName))
        {
            supervisorRights.ShouldContain(
                permission,
                $"'{skillName}' asks for '{permission}', which the Authorised role does not expand " +
                "to, so the skill is Admin-only however the other entries read.");
        }
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
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null)
        {
            var candidate = Path.Combine(
                new[] { directory.FullName }.Concat(DefinitionsRelativePath).Concat([fileName]).ToArray());

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {fileName} above the test directory.");
    }
}
