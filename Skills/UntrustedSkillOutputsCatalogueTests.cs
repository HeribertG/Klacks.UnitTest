// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Rot guard for the untrusted-output registry. UntrustedSkillOutputs classifies by skill NAME, so a
/// rename in a seed file would silently drop the untrusted framing from a skill whose results carry
/// externally authored content — the defense would disappear without any test turning red. This guard
/// asserts that every curated entry still exists in the seeded skill catalogue (core definitions plus
/// every plugin feature seed), and that the curated names are unique and lower-case, matching the
/// naming convention every seeded skill follows.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UntrustedSkillOutputsCatalogueTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string SkillsJsonProperty = "skills";
    private const string SkillNameJsonProperty = "name";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] PluginsFeaturesRelativePath =
    [
        "Klacks.Api", "Plugins", "Features"
    ];

    [Test]
    public void EveryCuratedUntrustedSkill_ExistsInTheSeededCatalogue()
    {
        var catalogue = LoadSeededSkillNames();
        catalogue.ShouldNotBeEmpty();

        var missing = UntrustedSkillOutputs.All
            .Where(name => !catalogue.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        missing.ShouldBeEmpty(
            "UntrustedSkillOutputs lists skills that no longer exist in any skill-seeds.json. "
            + "A rename silently disables the prompt-injection framing for that skill: "
            + string.Join(", ", missing));
    }

    [Test]
    public void CuratedNames_FollowTheSeedNamingConvention()
    {
        foreach (var name in UntrustedSkillOutputs.All)
        {
            name.ShouldBe(name.ToLowerInvariant());
            name.Trim().ShouldBe(name);
        }
    }

    private static HashSet<string> LoadSeededSkillNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var coreFile = LocateDefinitionsFile(SkillSeedsFileName);
        AddNamesFromFile(coreFile, names);

        var featuresDir = TryLocateDir(PluginsFeaturesRelativePath);
        if (featuresDir != null)
        {
            foreach (var pluginSeed in Directory.GetFiles(featuresDir, SkillSeedsFileName, SearchOption.AllDirectories))
            {
                AddNamesFromFile(pluginSeed, names);
            }
        }

        return names;
    }

    private static void AddNamesFromFile(string path, HashSet<string> names)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var skills = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty(SkillsJsonProperty, out var skillsElement) ? skillsElement : default;

        if (skills.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var skill in skills.EnumerateArray())
        {
            if (skill.TryGetProperty(SkillNameJsonProperty, out var nameElement)
                && nameElement.GetString() is { Length: > 0 } name)
            {
                names.Add(name);
            }
        }
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
}
