// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the seeded rights of the group-creating skills to the owner decision of 2026-09-21: an Admin and
/// a Supervisor (role Authorised) may create groups, a caller without a role may not, and deleting a
/// group stays with the Admin. create_group demanded CanEditSettings, which only the Admin holds, while
/// the REST endpoint it writes through (InputBaseController.Post, Admin plus Authorised) and the Angular
/// button (CanCreateGroups) both accepted a Supervisor - the assistant refused what the UI offered.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class GroupCreationSkillPermissionTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string CreateGroupSkill = "create_group";
    private const string DeleteGroupSkill = "delete_group";
    private const string PartitionClientsByAddressSkill = "partition_clients_by_address";

    private const string VersionBumpReminder =
        "Remember the version bump when changing this - SkillSeedLoader skips a seed whose version did " +
        "not increase, so the change never reaches an existing database.";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    /// <summary>The seeded skills whose execution creates group rows.</summary>
    private static readonly string[] GroupCreatingSkills =
    [
        CreateGroupSkill,
        PartitionClientsByAddressSkill
    ];

    [TestCaseSource(nameof(GroupCreatingSkills))]
    public void GroupCreatingSkill_AsksForTheGroupCreationRight_NotForSettings(string skillName)
    {
        var permissions = SeededPermissionsOf(skillName);

        permissions.ShouldContain(
            Permissions.CanCreateGroups,
            $"'{skillName}' creates groups, which an Admin and a Supervisor may both do. {VersionBumpReminder}");

        permissions.ShouldNotContain(
            Permissions.CanEditSettings,
            $"'{skillName}' must not be gated on CanEditSettings - only the Admin holds it, so a " +
            $"Supervisor would be refused a group the UI lets them create. {VersionBumpReminder}");
    }

    [TestCaseSource(nameof(GroupCreatingSkills))]
    public void GroupCreatingSkill_EveryRightItAsksFor_IsHeldByASupervisor(string skillName)
    {
        var supervisorRights = Permissions.ExpandRoles(new[] { Roles.Authorised });

        foreach (var permission in SeededPermissionsOf(skillName))
        {
            supervisorRights.ShouldContain(
                permission,
                $"'{skillName}' asks for '{permission}', which the Authorised role does not expand to, " +
                "so the skill is Admin-only however the other entries read.");
        }
    }

    [TestCaseSource(nameof(GroupCreatingSkills))]
    public void GroupCreatingSkill_IsNotReachableFromThePlannerFloor(string skillName)
    {
        var floorRights = Permissions.ExpandRoles(Array.Empty<string>());

        SeededPermissionsOf(skillName).All(floorRights.Contains).ShouldBeFalse(
            $"'{skillName}' would be reachable by every authenticated caller: the Planer floor already " +
            "covers every right it asks for. Creating a group needs a role.");
    }

    [Test]
    public void DeleteGroupSkill_StaysWithTheAdmin()
    {
        SeededPermissionsOf(DeleteGroupSkill).ShouldContain(
            Permissions.CanEditSettings,
            $"Deleting a group stays an Admin action (owner decision 2026-09-21). {VersionBumpReminder}");

        Permissions.ExpandRoles(new[] { Roles.Authorised }).ShouldNotContain(
            Permissions.CanEditSettings,
            "The gate above only holds as long as the Authorised role does not expand to CanEditSettings.");
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
