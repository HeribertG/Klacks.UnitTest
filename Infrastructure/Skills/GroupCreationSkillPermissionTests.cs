// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the seeded rights of the group-creating skills to the owner decision of 2026-11-07 (roles are work
/// areas, superseding 2026-09-21): the group structure is a setting, so only the Admin creates root groups,
/// regroups clients into new groups and deletes groups. The regrouping skills ask for CanCreateGroups and
/// delete_group for CanDeleteGroups, which the Authorised role does not expand to. The one exception (owner
/// decision 2026-11-08): create_group asks for CanEditSubgroups, which the supervisor holds, because the Groups
/// Post handler behind it only lets a caller without the Admin role create a subgroup under a visible group.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class GroupCreationSkillPermissionTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string CreateGroupSkill = "create_group";
    private const string DeleteGroupSkill = "delete_group";
    private const string PartitionClientsByAddressSkill = "partition_clients_by_address";
    private const string PartitionClientsByQualificationSkill = "partition_clients_by_qualification";

    private const string VersionBumpReminder =
        "Remember the version bump when changing this - SkillSeedLoader skips a seed whose version did " +
        "not increase, so the change never reaches an existing database.";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    /// <summary>The seeded skills that regroup clients into new groups; create_group is pinned on its own.</summary>
    private static readonly string[] GroupCreatingSkills =
    [
        PartitionClientsByAddressSkill,
        PartitionClientsByQualificationSkill
    ];

    [TestCaseSource(nameof(GroupCreatingSkills))]
    public void GroupCreatingSkill_AsksForTheGroupCreationRight_NotForSettings(string skillName)
    {
        var permissions = SeededPermissionsOf(skillName);

        permissions.ShouldContain(
            Permissions.CanCreateGroups,
            $"'{skillName}' creates groups and asks for the group-creation right. {VersionBumpReminder}");

        permissions.ShouldNotContain(
            Permissions.CanEditSettings,
            $"'{skillName}' is gated on the group right, not on CanEditSettings, so the seed and the Ui " +
            $"button read the same right. {VersionBumpReminder}");
    }

    [TestCaseSource(nameof(GroupCreatingSkills))]
    public void GroupCreatingSkill_IsNotReachableForASupervisor(string skillName)
    {
        var supervisorRights = Permissions.ExpandRoles(new[] { Roles.Authorised });

        SeededPermissionsOf(skillName).All(supervisorRights.Contains).ShouldBeFalse(
            $"'{skillName}' creates groups, which is a setting and Admin-only (owner decision 2026-11-07).");
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
    public void CreateGroupSkill_AsksForTheSubgroupRight_SoTheSupervisorReachesIt()
    {
        SeededPermissionsOf(CreateGroupSkill).ShouldBe(
            new List<string> { Permissions.CanEditSubgroups },
            $"create_group is gated on CanEditSubgroups (owner decision 2026-11-08). {VersionBumpReminder}");

        Permissions.ExpandRoles(new[] { Roles.Authorised }).ShouldContain(Permissions.CanEditSubgroups);
        Permissions.ExpandRoles(Array.Empty<string>()).ShouldNotContain(
            Permissions.CanEditSubgroups,
            "Creating a subgroup is supervisor work; the Planer floor must not reach create_group.");
    }

    [Test]
    public void CreateGroupSkill_IsBoundToTheGroupVisibilityOfAnIdentifiedCaller()
    {
        GroupVisibilityBoundSkills.Contains(CreateGroupSkill).ShouldBeTrue(
            "A supervisor's create_group decides through the group visibility guard, which treats a missing user as " +
            "unrestricted; the executor must refuse a non-admin call without an identified user.");
    }

    [Test]
    public void DeleteGroupSkill_AsksForTheGroupDeletionRight_AndStaysWithTheAdmin()
    {
        SeededPermissionsOf(DeleteGroupSkill).ShouldBe(
            new List<string> { Permissions.CanDeleteGroups },
            $"delete_group is gated on CanDeleteGroups like the Ui delete button. {VersionBumpReminder}");

        Permissions.ExpandRoles(new[] { Roles.Authorised }).ShouldNotContain(
            Permissions.CanDeleteGroups,
            "Deleting a group stays an Admin action; the gate only holds while the Authorised role does not " +
            "expand to CanDeleteGroups.");
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
