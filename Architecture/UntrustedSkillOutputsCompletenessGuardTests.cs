// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Completeness guard for UntrustedSkillOutputs. The registry is curated by hand, and SkillResult.Data is an
/// anonymous object, so no attribute or DTO check can tell that a read skill returns free text. This guard
/// therefore scans the C# source of every seeded Read/Explain skill (resolved by reflection over
/// SkillImplementationAttribute, then to its source file by class name) for free-text member names such as
/// Note, Information, Description, Content, Comment, Reason, Utterance, Text, Body or Subject, after
/// string literals and comments are removed. A hit means the skill must either be listed in
/// UntrustedSkillOutputs or carry a written justification in the allowlist below. A false alarm costs one allowlist
/// line; a missing entry costs a prompt-injection channel, so the guard errs on the side of alarms. A stale
/// allowlist entry (skill gone, now untrusted, or no longer matching) fails as well.
///
/// Person names, company names and addresses are deliberately outside the scan: they appear in almost every
/// result, so marking them would show the untrusted banner on nearly every turn and wear down its signal.
///
/// What this guard does NOT see: skills without a C# class (knowledge, generic-list and UI-action handlers carry no
/// user data; settings readers come from settings-reader-skills.json and expose fixed administrator settings), plugin
/// skills implemented in other assemblies, and skills that return a whole resource object without naming a
/// free-text property in their own file.
/// </summary>

using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.Api.Domain.Attributes;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.Infrastructure.Skills;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class UntrustedSkillOutputsCompletenessGuardTests
{
    private const string CoreSeedsFileName = "skill-seeds.json";
    private const string SettingsReaderFileName = "settings-reader-skills.json";
    private const string SkillsJsonProperty = "skills";
    private const string NameJsonProperty = "name";
    private const string ExecutionTypeJsonProperty = "executionType";
    private const string HandlerTypeJsonProperty = "handlerType";
    private const string UiActionExecutionType = "UiAction";
    private const string SourceFilePattern = "*.cs";
    private const string LineCommentPrefix = "//";
    private const string UsingPrefix = "using ";
    private const string NamespacePrefix = "namespace ";
    private const int MinimumResolvedSkills = 100;

    private static readonly string[] SkippedDirectories = ["bin", "obj", "Migrations"];

    private static readonly Regex StringLiteral = new(
        "\\$?@?\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.Compiled);

    private static readonly Regex FreeTextMember = new(
        @"(?<![A-Za-z0-9_])(?<member>Note|Notes|Information|Description|Comment|Comments|Content|Reason|Utterance|Text|Body|BodyText|Remark|Remarks|Subject|Annotation|Annotations|Memo|Title)(?![A-Za-z0-9_(])",
        RegexOptions.Compiled);

    private static readonly Regex SystemTextNamespace = new(
        @"\bSystem\.Text(?:\.[A-Za-z_][A-Za-z0-9_]*)*", RegexOptions.Compiled);

    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Lazy<ScanResult> Scan = new(ScanReadSkills);

    private static readonly Regex ClassDeclaration = new(
        @"\b(?:class|record)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, string> TrustedFreeTextReaders =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["diagnose_work_booking"] = "Reason, Remedy and Notes are fixed server sentences (WorkBookingDiagnosisTexts) filled only with person and shift names and dates; Comment is a validation key.",
            ["find_replacement"] = "Comment and Reason are texts the rule engine generates from fixed templates, not typed in by users.",
            ["get_ai_guidelines"] = "Guidelines are written with the settings-edit right, which only administrators hold; an administrator injects only themselves.",
            ["get_ai_soul"] = "Soul sections are written with the settings-edit right, which only administrators hold.",
            ["get_data_retention_settings"] = "Description is a constant field explanation compiled into the skill.",
            ["get_general_settings"] = "Description is a constant field explanation compiled into the skill.",
            ["get_period_close_schedule"] = "Reason is a system-generated classification of why a group closes or does not close automatically.",
            ["get_setup_guidance"] = "Reason is a system-generated explanation of the time-zone proposal.",
            ["list_export_format_overrides"] = "Notes are written by set_export_format_override, which is administrator-only.",
            ["list_feature_plugins"] = "Descriptions come from plugin manifests that only administrators install.",
            ["list_open_wizard_jobs"] = "Reason is a system-generated status explanation of the wizard job.",
            ["list_period_audit_log"] = "Reasons are typed when a period is closed or reopened, and both skills are administrator-only.",
            ["list_qualifications"] = "Qualification descriptions are written with the settings-edit right, which only administrators hold.",
            ["list_report_templates"] = "Report templates are written with the settings-edit right, which only administrators hold.",
            ["list_transcription_dictionary_entries"] = "Dictionary entries are written with the settings-edit right, which only administrators hold.",
            ["preview_planning_profile"] = "Note is a system-generated sentence describing the planned rule changes.",
            ["propose_grouping"] = "Reason is the system-generated classification key of why a client stays unassigned."
        };

    private static readonly IReadOnlyDictionary<string, string> UnscannedPluginSkills =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["list_messaging_providers"] = "Lists the configured messaging providers and their status; implemented in the messaging plugin assembly, administrator-configured."
        };

    [Test]
    public void EveryReadSkillProjectingFreeText_IsUntrustedOrJustified()
    {
        var scan = Scan.Value;

        scan.ResolvedSkills.ShouldBeGreaterThan(
            MinimumResolvedSkills,
            "Too few skill implementations were resolved to source files; a green result would prove nothing.");

        var uncovered = scan.FreeTextMembersBySkill
            .Where(entry => !UntrustedSkillOutputs.Contains(entry.Key) && !TrustedFreeTextReaders.ContainsKey(entry.Key))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"{entry.Key} ({string.Join(", ", entry.Value)})")
            .ToList();

        uncovered.ShouldBeEmpty(
            "These Read/Explain skills project free-text members but are neither in UntrustedSkillOutputs nor in the "
            + "TrustedFreeTextReaders allowlist. If a user below the administrator can write that text, add the skill to "
            + "UntrustedSkillOutputs; if the text is system-generated, constant or administrator-only, add it to the allowlist "
            + "with the reason: " + string.Join("; ", uncovered));
    }

    [Test]
    public void TrustedFreeTextReaders_HaveAReasonAndStillMatch()
    {
        var scan = Scan.Value;
        var problems = new List<string>();

        foreach (var (skill, reason) in TrustedFreeTextReaders)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                problems.Add($"{skill}: no reason");
            }

            if (UntrustedSkillOutputs.Contains(skill))
            {
                problems.Add($"{skill}: already untrusted, remove it from the allowlist");
            }

            if (!scan.FreeTextMembersBySkill.ContainsKey(skill))
            {
                problems.Add($"{skill}: no longer projects free text or is no longer a scanned Read/Explain skill");
            }
        }

        problems.ShouldBeEmpty(string.Join("; ", problems));
    }

    [TestCase("list_schedule_notes")]
    [TestCase("get_ai_memories")]
    [TestCase("search_client_absences")]
    [TestCase("list_expenses")]
    [TestCase("list_navigation_feedback")]
    [TestCase("get_group_details")]
    [TestCase("get_shift_details")]
    public void TheScan_StillSeesTheKnownFreeTextReaders(string skillName)
    {
        Scan.Value.FreeTextMembersBySkill.ShouldContainKey(skillName);
    }

    [TestCase("list_groups")]
    [TestCase("list_groups_hierarchical")]
    public void TheGroupLists_ReturnNoDescription_SoTheyNeedNeitherTheCatalogueNorTheAllowlist(string skillName)
    {
        Scan.Value.FreeTextMembersBySkill.ShouldNotContainKey(skillName);
        UntrustedSkillOutputs.Contains(skillName).ShouldBeFalse();
        TrustedFreeTextReaders.ContainsKey(skillName).ShouldBeFalse();
    }

    [TestCase("a.Description.Trim()", "Description")]
    [TestCase("select new { x.Note }", "Note")]
    [TestCase("/* x.Comment */ var y = 1;", null)]
    [TestCase("var s = new System.Text.StringBuilder();", null)]
    [TestCase("// x.Reason", null)]
    public void TheMemberScan_ReadsMemberAccessChains_AndSkipsCommentsAndNamespaces(string source, string? expectedMember)
    {
        var members = FindFreeTextMembers(source);

        if (expectedMember == null)
        {
            members.ShouldBeEmpty();
        }
        else
        {
            members.ShouldContain(expectedMember);
        }
    }

    [Test]
    public void SkillsWithoutASourceClass_AreCodelessByDesign()
    {
        var scan = Scan.Value;

        var unexplained = scan.UnresolvedSkills
            .Where(name => !UntrustedSkillOutputs.Contains(name) && !UnscannedPluginSkills.ContainsKey(name))
            .ToList();

        unexplained.ShouldBeEmpty(
            "These Read/Explain skills are seeded as plain skills but no class in Klacks.Api carries their SkillImplementation "
            + "attribute, so the free-text scan cannot see them. Seeds with a handler type, UI actions and generated settings "
            + "readers are exempt; anything else is either listed in UntrustedSkillOutputs or needs a justified entry in "
            + "UnscannedPluginSkills: " + string.Join(", ", unexplained));
    }

    [Test]
    public void UnscannedPluginSkills_StillAreUnresolvedAndNotUntrusted()
    {
        var unresolved = Scan.Value.UnresolvedSkills;
        var stale = UnscannedPluginSkills.Keys
            .Where(name => !unresolved.Contains(name) || UntrustedSkillOutputs.Contains(name))
            .ToList();

        stale.ShouldBeEmpty("Remove these entries from UnscannedPluginSkills: " + string.Join(", ", stale));
    }

    private static ScanResult ScanReadSkills()
    {
        var implementationTypes = LoadImplementationTypes();
        var sourceFiles = LoadSourceFilesByClassName();
        var codeless = LoadCodelessSkillNames();

        var freeTextBySkill = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var unresolved = new List<string>();
        var resolved = 0;

        foreach (var skill in SkillSeedCatalog.EnabledSkills().Where(IsReadOrExplain))
        {
            if (codeless.Contains(skill.Name))
            {
                continue;
            }

            if (!implementationTypes.TryGetValue(skill.Name, out var type)
                || !sourceFiles.TryGetValue(type.Name, out var file))
            {
                unresolved.Add(skill.Name);
                continue;
            }

            resolved++;
            var members = FindFreeTextMembers(File.ReadAllText(file));
            if (members.Count > 0)
            {
                freeTextBySkill[skill.Name] = members;
            }
        }

        return new ScanResult(freeTextBySkill, unresolved.OrderBy(name => name, StringComparer.Ordinal).ToList(), resolved);
    }

    private static bool IsReadOrExplain(SeedSkill skill) =>
        skill.Effect is SkillEffect.Read or SkillEffect.Explain;

    private static List<string> FindFreeTextMembers(string source)
    {
        var members = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var rawLine in BlockComment.Replace(source, string.Empty).Split('\n'))
        {
            var line = rawLine.TrimStart();
            if (line.StartsWith(LineCommentPrefix, StringComparison.Ordinal)
                || line.StartsWith(UsingPrefix, StringComparison.Ordinal)
                || line.StartsWith(NamespacePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var code = SystemTextNamespace.Replace(StringLiteral.Replace(line, "\"\""), string.Empty);
            var commentStart = code.IndexOf(LineCommentPrefix, StringComparison.Ordinal);
            if (commentStart >= 0)
            {
                code = code[..commentStart];
            }

            foreach (Match match in FreeTextMember.Matches(code))
            {
                members.Add(match.Groups["member"].Value);
            }
        }

        return [.. members];
    }

    private static Dictionary<string, Type> LoadImplementationTypes()
    {
        var types = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in typeof(SkillImplementationAttribute).Assembly.GetTypes())
        {
            var attribute = type.GetCustomAttribute<SkillImplementationAttribute>();
            if (attribute != null)
            {
                types.TryAdd(attribute.SkillName, type);
            }
        }

        return types;
    }

    private static Dictionary<string, string> LoadSourceFilesByClassName()
    {
        var apiRoot = RepositoryRootLocator.ApiProject;
        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(apiRoot, SourceFilePattern, SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(apiRoot, file);
            var segments = relative.Split(Path.DirectorySeparatorChar);
            if (segments.Take(segments.Length - 1).Any(segment => SkippedDirectories.Contains(segment, StringComparer.Ordinal)))
            {
                continue;
            }

            foreach (var line in File.ReadLines(file))
            {
                var match = ClassDeclaration.Match(line);
                if (match.Success && !line.TrimStart().StartsWith(LineCommentPrefix, StringComparison.Ordinal))
                {
                    files.TryAdd(match.Groups["name"].Value, file);
                    break;
                }
            }
        }

        return files;
    }

    private static HashSet<string> LoadCodelessSkillNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var coreFile = RepositoryRootLocator.RequireFile("Klacks.Api", "Application", "Skills", "Definitions", CoreSeedsFileName);
        using (var core = JsonDocument.Parse(File.ReadAllText(coreFile)))
        {
            foreach (var skill in core.RootElement.GetProperty(SkillsJsonProperty).EnumerateArray())
            {
                var hasHandler = skill.TryGetProperty(HandlerTypeJsonProperty, out var handler)
                    && handler.ValueKind == JsonValueKind.String;
                var isUiAction = skill.TryGetProperty(ExecutionTypeJsonProperty, out var execution)
                    && execution.ValueKind == JsonValueKind.String
                    && string.Equals(execution.GetString(), UiActionExecutionType, StringComparison.Ordinal);

                if ((hasHandler || isUiAction) && skill.GetProperty(NameJsonProperty).GetString() is { } name)
                {
                    names.Add(name);
                }
            }
        }

        var readerFile = RepositoryRootLocator.RequireFile("Klacks.Api", "Application", "Skills", "Definitions", SettingsReaderFileName);
        using var readers = JsonDocument.Parse(File.ReadAllText(readerFile));
        foreach (var skill in readers.RootElement.GetProperty(SkillsJsonProperty).EnumerateArray())
        {
            if (skill.GetProperty(NameJsonProperty).GetString() is { } name)
            {
                names.Add(name);
            }
        }

        return names;
    }

    private sealed record ScanResult(
        IReadOnlyDictionary<string, List<string>> FreeTextMembersBySkill,
        IReadOnlyList<string> UnresolvedSkills,
        int ResolvedSkills);
}
