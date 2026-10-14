// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the seeded rights of the inbound-analysis skills to the owner decision of 2026-09-24: whoever may
/// close an open clarification round (close_clarification, CanManageAutomation, Admin plus Supervisor) may
/// also read what the pipeline made of the message (get_messenger_analysis). The reader was CanViewSettings,
/// which only the Admin holds. It returns the analysis summary, the employee, the date window and the
/// clarification question, never the message body. A caller without a role (Planer floor) still reaches
/// none of them. The e-mail side of that decision (get_email_analysis, list_emails, read_email,
/// list_emails_by_client) moved from CanManageAutomation to CanEditClients on 2026-10-14 (owner decision F5 of
/// the messaging and inbox opening): the rights of the whole inbox are pinned in SkillRestParityGateTests, and
/// the four readers are pinned below as unchanged in who may call them.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class InboundAnalysisSkillPermissionTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string VersionJsonProperty = "version";

    private const string VersionBumpReminder =
        "Remember the version bump when changing this - SkillSeedLoader skips a seed whose version did " +
        "not increase, so the change never reaches an existing database.";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] ClarificationSkills =
    [
        "get_messenger_analysis",
        "close_clarification"
    ];

    private static readonly string[] InboxReaders =
    [
        "get_email_analysis",
        "list_emails",
        "read_email",
        "list_emails_by_client"
    ];

    [TestCaseSource(nameof(ClarificationSkills))]
    public void ClarificationSkill_AsksExactlyForTheAutomationRight(string skillName)
    {
        SeededPermissionsOf(skillName).ShouldBe(
            [Permissions.CanManageAutomation],
            $"'{skillName}' must require CanManageAutomation and nothing else: the permissions are ANDed, so " +
            $"an extra CanViewSettings would keep the Supervisor out. {VersionBumpReminder}");
    }

    [TestCaseSource(nameof(ClarificationSkills))]
    public void ClarificationSkill_IsReachableByAdminAndSupervisor(string skillName)
    {
        var required = string.Join(",", SeededPermissionsOf(skillName));

        Permissions.HasAllRequiredPermissions(Permissions.ExpandRoles(new[] { Roles.Admin }), required).ShouldBeTrue();
        Permissions.HasAllRequiredPermissions(Permissions.ExpandRoles(new[] { Roles.Authorised }), required).ShouldBeTrue();
    }

    [TestCaseSource(nameof(ClarificationSkills))]
    public void ClarificationSkill_IsNotReachableFromThePlannerFloor(string skillName)
    {
        var required = string.Join(",", SeededPermissionsOf(skillName));

        Permissions.HasAllRequiredPermissions(Permissions.ExpandRoles(Array.Empty<string>()), required).ShouldBeFalse(
            $"'{skillName}' would be reachable by every authenticated caller: the Planer floor must not " +
            "hold CanManageAutomation.");
    }

    [TestCaseSource(nameof(InboxReaders))]
    public void InboxReader_StaysReachableByAdminAndSupervisor_AndNotByThePlannerFloor(string skillName)
    {
        var required = string.Join(",", SeededPermissionsOf(skillName));

        Permissions.HasAllRequiredPermissions(Permissions.ExpandRoles(new[] { Roles.Admin }), required).ShouldBeTrue();
        Permissions.HasAllRequiredPermissions(Permissions.ExpandRoles(new[] { Roles.Authorised }), required).ShouldBeTrue();
        Permissions.HasAllRequiredPermissions(Permissions.ExpandRoles(Array.Empty<string>()), required).ShouldBeFalse();
    }

    [TestCase("get_email_analysis", 6)]
    [TestCase("get_messenger_analysis", 2)]
    [TestCase("list_emails", 6)]
    [TestCase("read_email", 10)]
    [TestCase("list_emails_by_client", 5)]
    public void FormerlyAdminOnlyReader_CarriesTheBumpedSeedVersion(string skillName, int minimumVersion)
    {
        SeededVersionOf(skillName).ShouldBeGreaterThanOrEqualTo(
            minimumVersion,
            $"'{skillName}' changed its required permission with this version. {VersionBumpReminder}");
    }

    private static JsonElement SeededSkill(JsonDocument document, string skillName)
    {
        var skill = document.RootElement.GetProperty("skills")
            .EnumerateArray()
            .FirstOrDefault(s => s.TryGetProperty("name", out var n) && n.GetString() == skillName);

        skill.ValueKind.ShouldNotBe(JsonValueKind.Undefined, $"'{skillName}' is missing from the seed file.");
        return skill;
    }

    private static List<string> SeededPermissionsOf(string skillName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateDefinitionsFile()));

        return SeededSkill(document, skillName).GetProperty("requiredPermissions")
            .EnumerateArray()
            .Select(p => p.GetString() ?? string.Empty)
            .ToList();
    }

    private static int SeededVersionOf(string skillName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateDefinitionsFile()));

        return SeededSkill(document, skillName).GetProperty(VersionJsonProperty).GetInt32();
    }

    private static string LocateDefinitionsFile()
    {
        return RepositoryRootLocator.RequireFile([.. DefinitionsRelativePath, SkillSeedsFileName]);
    }
}
