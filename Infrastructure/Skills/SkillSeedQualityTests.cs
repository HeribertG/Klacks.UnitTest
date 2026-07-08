// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards trigger-keyword quality in skill-seeds.json: no two skills may declare the same set of
/// trigger keywords. Word-identical keyword lists make the deterministic Tier1 keyword guarantee
/// (SkillMatchingEngine) unable to distinguish the skills, so the guarantee cap fills with
/// alphabetically arbitrary picks instead of the skill the user actually asked for.
/// </summary>

using System.Text.Json;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SkillSeedQualityTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string KeywordSetSeparator = "\u0001";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    [Test]
    public void TriggerKeywords_NoTwoSkillsShareAnIdenticalKeywordSet()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateDefinitionsFile(SkillSeedsFileName)));

        var skillsByKeywordSet = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var skill in document.RootElement.GetProperty("skills").EnumerateArray())
        {
            if (!skill.TryGetProperty("triggerKeywords", out var keywords) ||
                keywords.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var normalizedTerms = keywords.EnumerateArray()
                .Select(k => (k.GetString() ?? string.Empty).Trim().ToLowerInvariant())
                .Where(k => k.Length > 0)
                .Distinct()
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

            if (normalizedTerms.Count == 0)
            {
                continue;
            }

            var setKey = string.Join(KeywordSetSeparator, normalizedTerms);
            var skillName = skill.GetProperty("name").GetString() ?? string.Empty;

            if (!skillsByKeywordSet.TryGetValue(setKey, out var names))
            {
                names = [];
                skillsByKeywordSet[setKey] = names;
            }

            names.Add(skillName);
        }

        var collisions = skillsByKeywordSet.Values
            .Where(names => names.Count > 1)
            .Select(names => string.Join(", ", names.OrderBy(n => n, StringComparer.Ordinal)))
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();

        collisions.ShouldBeEmpty(
            $"{SkillSeedsFileName} contains skills with identical triggerKeywords sets; the deterministic " +
            "keyword guarantee cannot rank them and falls back to alphabetical arbitrariness. Give each " +
            "skill action-specific keywords. Colliding groups: " + string.Join(" | ", collisions));
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
