// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the seeded rights of the inbound-analysis skills to the owner decision of 2026-09-24: whoever may
/// close an open clarification round (close_clarification, CanManageAutomation, Admin plus Supervisor) may
/// also read what the pipeline made of the message (get_email_analysis, get_messenger_analysis). Both
/// readers were CanViewSettings, which only the Admin holds. They return the analysis summary, the
/// employee, the date window and the clarification question, never the message body; the mail inbox they
/// draw on is open to every authenticated caller (InboxGuard is a feature gate, ReceivedEmailController
/// has no role), so the change widens no access to raw text. A caller without a role (Planer floor) still
/// reaches none of the three.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;

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
        "get_email_analysis",
        "get_messenger_analysis",
        "close_clarification"
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

    [TestCase("get_email_analysis", 5)]
    [TestCase("get_messenger_analysis", 2)]
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
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null)
        {
            var candidate = Path.Combine(
                new[] { directory.FullName }.Concat(DefinitionsRelativePath).Concat([SkillSeedsFileName]).ToArray());

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {SkillSeedsFileName} above the test directory.");
    }
}
