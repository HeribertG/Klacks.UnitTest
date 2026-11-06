// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Hubs;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Hubs;

/// <summary>
/// Binds each wizard start skill to the job hub its job streams on: for every role set (Admin, Authorised, User, no role = the
/// planner floor) the skill's requiredPermissions from skill-seeds.json admit the caller exactly when the hub's
/// [Authorize] policy does. The skill side is evaluated the way the assistant filters skills (the seed loader joins the
/// list with commas, Permissions.ExpandRoles turns the roles into rights, Permissions.HasAllRequiredPermissions decides);
/// the hub side through the real authorization service. A skill that may start a job whose hub refuses the caller (the
/// progress would never arrive), or a hub that admits callers the skill refuses, turns this red. A second test pins the
/// skills to Admin, so loosening skill and hub together is caught as well.
/// </summary>
[TestFixture]
public class WizardStartSkillHubRoleParityGuardTests
{
    private const string SkillsProperty = "skills";
    private const string NameProperty = "name";
    private const string RequiredPermissionsProperty = "requiredPermissions";
    private const string PermissionSeparator = ",";

    public static IEnumerable<TestCaseData> StartSkillsAndHubs()
    {
        yield return new TestCaseData("start_autowizard", typeof(AutoWizardJobHub));
        yield return new TestCaseData("start_wizard1", typeof(WizardJobHub));
        yield return new TestCaseData("start_wizard2", typeof(HarmonizerJobHub));
        yield return new TestCaseData("start_wizard3", typeof(HolisticHarmonizerJobHub));
    }

    private static readonly string[][] RoleSets =
    [
        [Roles.Admin],
        [Roles.Authorised],
        [Roles.User],
        [],
    ];

    [TestCaseSource(nameof(StartSkillsAndHubs))]
    public async Task StartSkill_AdmitsExactlyTheRolesItsJobHubAdmits(string skillName, Type hubType)
    {
        var requiredPermission = ReadRequiredPermission(skillName);

        foreach (var roles in RoleSets)
        {
            var skillAdmits = Permissions.HasAllRequiredPermissions(Permissions.ExpandRoles(roles), requiredPermission);
            var hubAdmits = await HubAuthorizationEvaluator.IsAuthorizedAsync(hubType, roles);

            skillAdmits.ShouldBe(
                hubAdmits,
                $"{skillName} (requiredPermissions '{requiredPermission}') and {hubType.Name} disagree for roles [{string.Join(PermissionSeparator, roles)}]");
        }
    }

    [TestCaseSource(nameof(StartSkillsAndHubs))]
    public void StartSkill_StaysAdminOnly(string skillName, Type hubType)
    {
        ReadRequiredPermission(skillName).ShouldBe(
            Roles.Admin,
            $"{skillName} no longer requires Admin; the parity test alone would stay green if {hubType.Name} and the skill were loosened together");
    }

    private static string? ReadRequiredPermission(string skillName)
    {
        var seedPath = RepositoryRootLocator.RequireFile(["Klacks.Api", "Application", "Skills", "Definitions", "skill-seeds.json"]);
        using var doc = JsonDocument.Parse(File.ReadAllText(seedPath));

        var skill = doc.RootElement.GetProperty(SkillsProperty).EnumerateArray()
            .Single(s => s.GetProperty(NameProperty).GetString() == skillName);

        if (!skill.TryGetProperty(RequiredPermissionsProperty, out var permissions) || permissions.GetArrayLength() == 0)
        {
            return null;
        }

        return string.Join(PermissionSeparator, permissions.EnumerateArray().Select(p => p.GetString()));
    }
}
