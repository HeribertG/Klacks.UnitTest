// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Quality gates for the per-language skill-synonym packs (Plugins/Languages/&lt;locale&gt;/skill-synonyms.json).
/// Orphan gate: every key must be an existing skill name from skill-seeds.json or a feature-plugin
/// skill-seeds.json — an orphan key is dead data that silently loses its language coverage (the ro
/// incident of 2026-07-07 shipped 51 orphan entries unnoticed). Disjointness gate: within one language
/// the same synonym phrase must not be registered for two different skills, because the deterministic
/// keyword match then boosts an arbitrary one of them. Collisions where all involved skills are
/// explain_page_* skills are exempt: those skills are disambiguated by the user's current page at
/// runtime and intentionally share generic phrases.
/// </summary>

using System.Text.Json;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SkillSynonymPackQualityTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string SkillSynonymsFileName = "skill-synonyms.json";
    private const string ExplainPageSkillPrefix = "explain_page_";
    private const int MaxReportedViolations = 25;

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] PluginsLanguagesRelativePath =
    [
        "Klacks.Api", "Plugins", "Languages"
    ];

    private static readonly string[] PluginsFeaturesRelativePath =
    [
        "Klacks.Api", "Plugins", "Features"
    ];

    public static IEnumerable<string> LocaleDirectories() =>
        Directory.GetDirectories(LocateDir(PluginsLanguagesRelativePath))
            .Select(d => new DirectoryInfo(d).Name)
            .OrderBy(name => name, StringComparer.Ordinal);

    [TestCaseSource(nameof(LocaleDirectories))]
    public void SkillSynonymKeys_MustReferenceExistingSkills(string locale)
    {
        var knownSkills = LoadKnownSkillNames();

        var orphans = LoadSkillSynonymPack(locale).Keys
            .Where(key => !knownSkills.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        orphans.ShouldBeEmpty(
            $"{locale}/{SkillSynonymsFileName} contains {orphans.Count} key(s) that are no skill in " +
            $"{SkillSeedsFileName} or any feature-plugin skill-seeds.json. Orphan keys are dead data and " +
            "usually mean the pack was generated against stale seeds. Orphans: " +
            string.Join(", ", orphans.Take(MaxReportedViolations)) +
            (orphans.Count > MaxReportedViolations ? $", ... and {orphans.Count - MaxReportedViolations} more" : string.Empty));
    }

    [TestCaseSource(nameof(LocaleDirectories))]
    public void SkillSynonyms_MustBeDisjointAcrossSkills_PerLanguage(string locale)
    {
        var skillsByPhrase = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var (skill, phrases) in LoadSkillSynonymPack(locale))
        {
            foreach (var phrase in (phrases ?? []).Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                var normalized = phrase.Trim().ToLowerInvariant();
                if (!skillsByPhrase.TryGetValue(normalized, out var skills))
                {
                    skills = new HashSet<string>(StringComparer.Ordinal);
                    skillsByPhrase[normalized] = skills;
                }

                skills.Add(skill);
            }
        }

        var violations = skillsByPhrase
            .Where(kv => kv.Value.Count > 1)
            .Where(kv => !kv.Value.All(skill => skill.StartsWith(ExplainPageSkillPrefix, StringComparison.Ordinal)))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"'{kv.Key}' -> {string.Join(", ", kv.Value.OrderBy(s => s, StringComparer.Ordinal))}")
            .ToList();

        violations.ShouldBeEmpty(
            $"{locale}/{SkillSynonymsFileName} registers the same synonym phrase for multiple skills; the " +
            "deterministic keyword match then boosts an arbitrary one. Assign each phrase to exactly one " +
            "skill (explain_page_* skills are exempt because the current page disambiguates them). " +
            "Violations: " + string.Join("; ", violations.Take(MaxReportedViolations)) +
            (violations.Count > MaxReportedViolations ? $"; ... and {violations.Count - MaxReportedViolations} more" : string.Empty));
    }

    private static HashSet<string> LoadKnownSkillNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        using (var document = JsonDocument.Parse(File.ReadAllText(LocateDefinitionsFile(SkillSeedsFileName))))
        {
            foreach (var skill in document.RootElement.GetProperty("skills").EnumerateArray())
            {
                var name = skill.GetProperty("name").GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }

        var featuresDir = TryLocateDir(PluginsFeaturesRelativePath);
        if (featuresDir == null)
        {
            return names;
        }

        foreach (var pluginDir in Directory.GetDirectories(featuresDir))
        {
            var seedFile = Path.Combine(pluginDir, SkillSeedsFileName);
            if (!File.Exists(seedFile))
            {
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(seedFile));
            foreach (var skill in document.RootElement.EnumerateArray())
            {
                var name = skill.GetProperty("name").GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    private static Dictionary<string, List<string>> LoadSkillSynonymPack(string locale)
    {
        var file = Path.Combine(LocateDir(PluginsLanguagesRelativePath), locale, SkillSynonymsFileName);
        if (!File.Exists(file))
        {
            return new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }

        return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(file))
            ?? new Dictionary<string, List<string>>(StringComparer.Ordinal);
    }

    private static string LocateDir(string[] relativePath) =>
        TryLocateDir(relativePath)
        ?? throw new DirectoryNotFoundException(
            $"Could not locate {string.Join('/', relativePath)} by walking up from the test base directory.");

    private static string? TryLocateDir(string[] relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var segments = new List<string> { dir.FullName };
            segments.AddRange(relativePath);
            var candidate = Path.Combine(segments.ToArray());
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static string LocateDefinitionsFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var segments = new List<string> { dir.FullName };
            segments.AddRange(DefinitionsRelativePath);
            segments.Add(fileName);
            var candidate = Path.Combine(segments.ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {string.Join('/', DefinitionsRelativePath)}/{fileName} by walking up from the test base directory.");
    }
}
