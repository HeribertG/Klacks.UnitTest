// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Parity gate for skill-relation-seeds.json: every edge must reference two skills that exist in
/// skill-seeds.json (or a feature-plugin skill-seeds.json), no edge may be self-referential, and
/// the (skillA, skillB, type) key must be unique so the insert-only loader stays deterministic.
/// </summary>

using System.Text.Json;
using Klacks.Api.Infrastructure.Persistence.Seed.Models;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SkillRelationSeedParityTests
{
    private const string RelationSeedsFileName = "skill-relation-seeds.json";
    private const string SkillSeedsFileName = "skill-seeds.json";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] PluginsFeaturesRelativePath =
    [
        "Klacks.Api", "Plugins", "Features"
    ];

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Test]
    public void EveryRelationSeed_ReferencesTwoDistinctExistingSkills()
    {
        var relations = LoadRelationSeeds();
        var knownSkills = LoadAllSeededSkillNames();

        var violations = new List<string>();
        foreach (var relation in relations)
        {
            if (string.Equals(relation.SkillAName, relation.SkillBName, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"Self-referential edge: {relation.SkillAName}");
            }

            if (!knownSkills.Contains(relation.SkillAName))
            {
                violations.Add($"Unknown skillAName: {relation.SkillAName}");
            }

            if (!knownSkills.Contains(relation.SkillBName))
            {
                violations.Add($"Unknown skillBName: {relation.SkillBName}");
            }
        }

        violations.ShouldBeEmpty();
    }

    [Test]
    public void RelationSeedKeys_AreUnique()
    {
        var relations = LoadRelationSeeds();

        var duplicates = relations
            .GroupBy(r => (r.SkillAName, r.SkillBName, r.Type))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.SkillAName}|{g.Key.SkillBName}|{g.Key.Type}")
            .ToList();

        duplicates.ShouldBeEmpty();
    }

    private static List<SkillRelationSeedDefinition> LoadRelationSeeds()
    {
        var path = LocateDefinitionsFile(RelationSeedsFileName);
        var seedFile = JsonSerializer.Deserialize<SkillRelationSeedFile>(File.ReadAllText(path), JsonReadOptions);
        seedFile.ShouldNotBeNull();
        seedFile!.Relations.ShouldNotBeEmpty();
        return seedFile.Relations;
    }

    private static HashSet<string> LoadAllSeededSkillNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddSkillNamesFrom(LocateDefinitionsFile(SkillSeedsFileName), names);

        var pluginsDir = TryLocateDir(PluginsFeaturesRelativePath);
        if (pluginsDir != null)
        {
            foreach (var pluginDir in Directory.GetDirectories(pluginsDir))
            {
                var pluginSeedFile = Path.Combine(pluginDir, SkillSeedsFileName);
                if (File.Exists(pluginSeedFile))
                {
                    AddSkillNamesFrom(pluginSeedFile, names);
                }
            }
        }

        return names;
    }

    private static void AddSkillNamesFrom(string filePath, HashSet<string> names)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
        var skills = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : doc.RootElement.GetProperty("skills");
        foreach (var skill in skills.EnumerateArray())
        {
            var name = skill.GetProperty("name").GetString();
            if (!string.IsNullOrWhiteSpace(name))
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
