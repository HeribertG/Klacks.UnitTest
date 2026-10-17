// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the structure and wording of the multi-level explanation markers (&lt;!-- level:... --&gt;) in the curated
/// knowledge happens: the first marker is always the short stage, the pitfalls stage occurs at most once and is the
/// last one, only known stage names are used, a happen carries markers exactly when its skill declares a level
/// parameter, the body never claims an undo function Klacks does not have and never names the developer-only wizard
/// shortcut, and the priority topics keep a pitfalls stage. Markers are parsed exactly like
/// KnowledgeHappenExecutor does (prefix and suffix from KnowledgeHappenLevels, text in between trimmed).
/// Undo wording that is legitimate (a denial, or the separate Klacksy counter-action feature) is listed in
/// <see cref="AllowedUndoLines"/>; an entry that no longer matches fails the stale-entry test.
/// </summary>

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class KnowledgeHappenLevelStructureGuardTests
{
    private const string KnowledgeRelativePath = "Klacks.Api/Infrastructure/Persistence/Seed/KlacksyKnowledge";
    private const string SkillSeedsRelativePath = "Klacks.Api/Application/Skills/Definitions/skill-seeds.json";
    private const string MarkdownSearchPattern = "*.md";
    private const string FrontmatterDelimiter = "---";
    private const string SkillsPropertyName = "skills";
    private const string NamePropertyName = "name";
    private const string ParametersPropertyName = "parameters";
    private const string KeySeparator = "|";
    private const int MinimumHappenCount = 60;
    private const int MinimumMarkedHappenCount = 30;

    private static readonly string[] DeveloperWizardShortcuts = ["Ctrl+Shift+H", "Strg+Umschalt+H"];

    private static readonly string[] ForbiddenUndoTokens = ["Undo", "Strg+Z", "Ctrl+Z", "Rückgängig-Schaltfläche"];

    private static readonly (string File, string LineFragment)[] AllowedUndoLines =
    [
        ("13-page-schedule.md", "ein Undo/Redo gibt es in Klacks nicht"),
        ("25-klacksy-autonomy.md", "propose the counter-action to undo the most recent"),
        ("25-klacksy-autonomy.md", "It does **not** undo anything on its own"),
        ("25-klacksy-autonomy.md", "\"Undo what you just did.\""),
    ];

    private static readonly string[] PriorityHappenNames =
    [
        "explain_shift_sporadic",
        "explain_shift_time_range",
        "explain_shift_container",
        "explain_shift_lifecycle_order_to_shift",
        "explain_address_management",
        "explain_page_shifts",
        "explain_page_employees",
        "explain_page_groups",
        "explain_contracts",
        "explain_group_model",
        "explain_group_hierarchy_inheritance",
        "explain_group_visibility_blind_spots",
        "explain_group_membership_validity",
        "explain_legal_entity_rule",
        "explain_membership_single_row",
        "explain_shift_classification",
        "explain_contract_setup",
    ];

    private static readonly Regex FrontmatterNameRegex = new(@"^name:\s*(?<name>\S+)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex MarkerRegex = new(
        Regex.Escape(KnowledgeHappenLevels.MarkerPrefix) + "(?<level>.*?)" + Regex.Escape(KnowledgeHappenLevels.MarkerSuffix),
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex UndoTokenRegex = new(
        string.Join(KeySeparator, ForbiddenUndoTokens.Select(token => @"(?<!\w)" + Regex.Escape(token) + @"(?!\w)")),
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] KnownMarkerNames =
    [
        KnowledgeHappenLevels.Short,
        KnowledgeHappenLevels.Elements,
        KnowledgeHappenLevels.Effects,
        KnowledgeHappenLevels.Pitfalls,
    ];

    private sealed record Happen(string File, string Name, string Body, IReadOnlyList<string> Markers);

    [Test]
    public void KnowledgeHappens_MustBeFoundAndMarked()
    {
        var happens = LoadHappens();

        happens.Count.ShouldBeGreaterThanOrEqualTo(
            MinimumHappenCount,
            "The happen loader found suspiciously few knowledge happens; the frontmatter format may have changed.");
        happens.Count(h => h.Markers.Count > 0).ShouldBeGreaterThanOrEqualTo(
            MinimumMarkedHappenCount,
            "The marker regex found suspiciously few level-marked happens; it may have stopped matching the marker format.");
    }

    [Test]
    public void MarkedHappen_FirstMarkerIsShort()
    {
        var violations = LoadHappens()
            .Where(h => h.Markers.Count > 0 && h.Markers[0] != KnowledgeHappenLevels.Short)
            .Select(h => $"{h.File}: first marker is '{h.Markers[0]}'")
            .ToList();

        violations.ShouldBeEmpty(
            "The first level marker of a marked happen must be '" + KnowledgeHappenLevels.Short + "': " + Join(violations));
    }

    [Test]
    public void Pitfalls_AtMostOnce_AndIsLastMarker()
    {
        var violations = new List<string>();
        foreach (var happen in LoadHappens().Where(h => h.Markers.Count > 0))
        {
            var count = happen.Markers.Count(m => m == KnowledgeHappenLevels.Pitfalls);
            if (count > 1)
            {
                violations.Add($"{happen.File}: '{KnowledgeHappenLevels.Pitfalls}' occurs {count} times");
            }
            else if (count == 1 && happen.Markers[^1] != KnowledgeHappenLevels.Pitfalls)
            {
                violations.Add($"{happen.File}: '{KnowledgeHappenLevels.Pitfalls}' is not the last marker");
            }
        }

        violations.ShouldBeEmpty(
            "The pitfalls stage may occur at most once and must be the last marker of a happen: " + Join(violations));
    }

    [Test]
    public void OnlyKnownMarkerNames()
    {
        var violations = LoadHappens()
            .SelectMany(h => h.Markers.Where(m => !KnownMarkerNames.Contains(m, StringComparer.Ordinal)).Select(m => $"{h.File}: '{m}'"))
            .ToList();

        violations.ShouldBeEmpty(
            "Unknown level marker names (allowed: " + string.Join(", ", KnownMarkerNames) + "): " + Join(violations));
    }

    [Test]
    public void MarkersIffSkillHasLevelParam()
    {
        var skillsWithLevel = LoadSkillsWithLevelParameter();
        var violations = new List<string>();

        foreach (var happen in LoadHappens())
        {
            var hasMarkers = happen.Markers.Count > 0;
            var hasLevel = skillsWithLevel.TryGetValue(happen.Name, out var skillHasLevel) && skillHasLevel;

            if (hasMarkers && !hasLevel)
            {
                violations.Add($"{happen.File} ({happen.Name}): has level markers but its skill has no '{KnowledgeHappenLevels.ParameterName}' parameter");
            }
            else if (!hasMarkers && hasLevel)
            {
                violations.Add($"{happen.File} ({happen.Name}): its skill has a '{KnowledgeHappenLevels.ParameterName}' parameter but the happen has no level markers");
            }
        }

        violations.ShouldBeEmpty(
            "A happen must carry level markers exactly when its skill declares a level parameter: " + Join(violations));
    }

    [Test]
    public void NoUndoClaims()
    {
        var violations = FindUndoLines()
            .Where(line => !AllowedUndoLines.Any(entry => entry.File == line.File && line.Text.Contains(entry.LineFragment, StringComparison.Ordinal)))
            .Select(line => $"{line.File}: {line.Text}")
            .ToList();

        violations.ShouldBeEmpty(
            "Klacks has no general undo/redo; the knowledge must not suggest one. Reword the line, or list a " +
            "legitimate denial in AllowedUndoLines: " + Join(violations));
    }

    [Test]
    public void AllowedUndoLines_MustStillApply()
    {
        var lines = FindUndoLines();

        var stale = AllowedUndoLines
            .Where(entry => !lines.Any(line => line.File == entry.File && line.Text.Contains(entry.LineFragment, StringComparison.Ordinal)))
            .Select(entry => KeyOf(entry.File, entry.LineFragment))
            .ToList();

        stale.ShouldBeEmpty(
            "These AllowedUndoLines entries no longer match an undo line (reworded or removed) and must be deleted: " + Join(stale));
    }

    [Test]
    public void NoDevWizardShortcut()
    {
        var violations = LoadHappens()
            .SelectMany(h => DeveloperWizardShortcuts
                .Where(shortcut => h.Body.Contains(shortcut, StringComparison.OrdinalIgnoreCase))
                .Select(shortcut => $"{h.File}: {shortcut}"))
            .ToList();

        violations.ShouldBeEmpty(
            "The developer-only wizard test shortcut must never appear in user knowledge: " + Join(violations));
    }

    [Test]
    public void PriorityTopics_HavePitfalls()
    {
        var happens = LoadHappens().ToDictionary(h => h.Name, StringComparer.Ordinal);
        var violations = new List<string>();

        foreach (var name in PriorityHappenNames)
        {
            if (!happens.TryGetValue(name, out var happen))
            {
                violations.Add($"{name}: no happen with this name");
            }
            else if (!happen.Markers.Contains(KnowledgeHappenLevels.Pitfalls, StringComparer.Ordinal))
            {
                violations.Add($"{name} ({happen.File}): no '{KnowledgeHappenLevels.Pitfalls}' stage");
            }
        }

        violations.ShouldBeEmpty("Priority topics must contain a pitfalls stage: " + Join(violations));
    }

    private static string Join(IEnumerable<string> entries) =>
        Environment.NewLine + string.Join(Environment.NewLine, entries);

    private static string KeyOf(string file, string fragment) =>
        string.Join(KeySeparator, file, fragment);

    private static List<(string File, string Text)> FindUndoLines()
    {
        var lines = new List<(string, string)>();

        foreach (var happen in LoadHappens())
        {
            foreach (var line in happen.Body.Split('\n'))
            {
                if (UndoTokenRegex.IsMatch(line))
                {
                    lines.Add((happen.File, line.Trim()));
                }
            }
        }

        return lines;
    }

    private static List<Happen> LoadHappens()
    {
        var directory = RepositoryRootLocator.RequireDirectory(KnowledgeRelativePath);
        var happens = new List<Happen>();

        foreach (var path in Directory.EnumerateFiles(directory, MarkdownSearchPattern).OrderBy(p => p, StringComparer.Ordinal))
        {
            var content = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
            var body = SplitFrontmatter(content, out var frontmatter);
            if (body == null)
            {
                continue;
            }

            var nameMatch = FrontmatterNameRegex.Match(frontmatter);
            if (!nameMatch.Success)
            {
                continue;
            }

            var markers = MarkerRegex.Matches(body)
                .Select(match => match.Groups["level"].Value.Trim())
                .ToList();

            happens.Add(new Happen(Path.GetFileName(path), nameMatch.Groups["name"].Value, body, markers));
        }

        return happens;
    }

    private static string? SplitFrontmatter(string content, out string frontmatter)
    {
        frontmatter = string.Empty;
        if (!content.StartsWith(FrontmatterDelimiter + "\n", StringComparison.Ordinal))
        {
            return null;
        }

        var end = content.IndexOf("\n" + FrontmatterDelimiter, FrontmatterDelimiter.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            return null;
        }

        frontmatter = content[FrontmatterDelimiter.Length..end];

        return content[(end + 1 + FrontmatterDelimiter.Length)..];
    }

    private static Dictionary<string, bool> LoadSkillsWithLevelParameter()
    {
        var path = RepositoryRootLocator.RequireFile(SkillSeedsRelativePath);
        using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        var skills = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var skill in document.RootElement.GetProperty(SkillsPropertyName).EnumerateArray())
        {
            var name = skill.GetProperty(NamePropertyName).GetString() ?? string.Empty;
            var hasLevel = skill.TryGetProperty(ParametersPropertyName, out var parameters)
                && parameters.ValueKind == JsonValueKind.Array
                && parameters.EnumerateArray().Any(p =>
                    string.Equals(p.GetProperty(NamePropertyName).GetString(), KnowledgeHappenLevels.ParameterName, StringComparison.Ordinal));
            skills[name] = hasLevel;
        }

        return skills;
    }
}
