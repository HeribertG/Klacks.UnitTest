// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the permission gate in front of the security-relevant skills that used to carry no
/// requiredPermissions at all — minting or revoking a personal access token, changing the autonomy
/// level, scheduling unattended runs and drafting a multi-step plan were reachable by every role,
/// including User. They must require CanPlan, which only Admin and Authorised hold. The genuinely
/// self-service skills must stay open, otherwise a plain user loses their own account and notes.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SecuritySensitiveSkillGateTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] MustRequireCanPlan =
    [
        "create_personal_access_token",
        "revoke_personal_access_token",
        "set_autonomy_level",
        "schedule_recurring_task",
        "create_plan"
    ];

    private static readonly string[] MustStayOpenForSelfService =
    [
        "update_my_account",
        "add_personal_memory",
        "stash_pending_note",
        "manage_pending_notes",
        "cancel_recurring_task"
    ];

    private static Dictionary<string, JsonElement> LoadSkillsByName()
    {
        var json = File.ReadAllText(LocateDefinitionsFile(SkillSeedsFileName));
        using var document = JsonDocument.Parse(json);

        return document.RootElement
            .GetProperty("skills")
            .EnumerateArray()
            .Where(s => s.TryGetProperty("name", out _))
            .ToDictionary(
                s => s.GetProperty("name").GetString()!,
                s => s.Clone(),
                StringComparer.Ordinal);
    }

    private static List<string> PermissionsOf(JsonElement skill)
    {
        if (!skill.TryGetProperty("requiredPermissions", out var permissions)
            || permissions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return permissions.EnumerateArray().Select(p => p.GetString() ?? string.Empty).ToList();
    }

    [Test]
    public void SecuritySensitiveSkills_RequireCanPlan()
    {
        var skills = LoadSkillsByName();

        foreach (var name in MustRequireCanPlan)
        {
            skills.ShouldContainKey(name);
            PermissionsOf(skills[name]).ShouldContain(
                Permissions.CanPlan,
                $"'{name}' mutates security-relevant state and must not be reachable by the User role. " +
                "Remember to bump the skill's version when changing this — SkillSeedLoader silently " +
                "skips a seed whose version did not increase.");
        }
    }

    [Test]
    public void CanPlan_IsHeldByAuthorisedAndAdminOnly()
    {
        Permissions.GetPermissionsForRole(Roles.Admin).ShouldContain(Permissions.CanPlan);
        Permissions.GetPermissionsForRole(Roles.Authorised).ShouldContain(Permissions.CanPlan);
        Permissions.GetPermissionsForRole(Roles.User).ShouldNotContain(Permissions.CanPlan);
    }

    [Test]
    public void SelfServiceSkills_StayOpenToEveryRole()
    {
        var skills = LoadSkillsByName();

        foreach (var name in MustStayOpenForSelfService)
        {
            skills.ShouldContainKey(name);
            PermissionsOf(skills[name]).ShouldBeEmpty(
                $"'{name}' only touches the caller's own data; gating it would lock a plain user out " +
                "of their own account.");
        }
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
