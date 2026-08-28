// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Reads the enabled skill definitions out of the seed files on disk, which is the same set the running
/// application feeds into the skill registry: the main catalogue, the generated settings-reader skills
/// and every installed feature plugin's own seed file. Category parsing mirrors
/// SkillRegistryInitializer.ParseCategory - an unknown or missing category falls back to a WRITE
/// category, so neither a seed typo nor a category the SkillCategory enum does not know (the messaging
/// plugin seeds "Communication") can make a mutating skill look read-only to a guard test.
/// </summary>

using System.Text.Json;

namespace Klacks.UnitTest.Infrastructure.Skills;

internal static class SkillSeedCatalog
{
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string SettingsReaderSkillsFileName = "settings-reader-skills.json";
    private const string SkillsJsonProperty = "skills";
    private const string SkillNameJsonProperty = "name";
    private const string SkillCategoryJsonProperty = "category";
    private const string SkillIsEnabledJsonProperty = "isEnabled";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] PluginsFeaturesRelativePath =
    [
        "Klacks.Api", "Plugins", "Features"
    ];

    private static readonly HashSet<SkillCategory> WriteCategories =
    [
        SkillCategory.Crud,
        SkillCategory.Action
    ];

    internal static IReadOnlyCollection<SeedSkill> EnabledSkills()
    {
        var skills = new Dictionary<string, SeedSkill>(StringComparer.OrdinalIgnoreCase);
        CollectFromDefinitionsFile(LocateDefinitionsFile(SkillSeedsFileName), skills);
        CollectFromDefinitionsFile(LocateDefinitionsFile(SettingsReaderSkillsFileName), skills);
        CollectFromPluginSeeds(skills);
        return skills.Values;
    }

    internal static bool IsWriteCategory(SkillCategory category) => WriteCategories.Contains(category);

    internal static SkillDescriptor ToDescriptor(SeedSkill skill)
    {
        return new SkillDescriptor(
            skill.Name,
            string.Empty,
            skill.Category,
            Array.Empty<SkillParameter>(),
            Array.Empty<string>(),
            Array.Empty<LLMCapability>(),
            null);
    }

    private static void CollectFromDefinitionsFile(string filePath, Dictionary<string, SeedSkill> skills)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(filePath));
        foreach (var element in document.RootElement.GetProperty(SkillsJsonProperty).EnumerateArray())
        {
            AddIfEnabled(element, skills);
        }
    }

    private static void CollectFromPluginSeeds(Dictionary<string, SeedSkill> skills)
    {
        var featuresDir = TryLocateDir(PluginsFeaturesRelativePath);
        if (featuresDir == null)
        {
            return;
        }

        foreach (var pluginDir in Directory.GetDirectories(featuresDir))
        {
            var seedFile = Path.Combine(pluginDir, SkillSeedsFileName);
            if (!File.Exists(seedFile))
            {
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(seedFile));
            foreach (var element in document.RootElement.EnumerateArray())
            {
                AddIfEnabled(element, skills);
            }
        }
    }

    private static void AddIfEnabled(JsonElement element, Dictionary<string, SeedSkill> skills)
    {
        if (!element.TryGetProperty(SkillNameJsonProperty, out var nameProperty) ||
            nameProperty.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var isEnabled = !element.TryGetProperty(SkillIsEnabledJsonProperty, out var enabled) || enabled.GetBoolean();
        if (!isEnabled)
        {
            return;
        }

        var name = nameProperty.GetString()!;
        skills.TryAdd(name, new SeedSkill(name, ParseCategory(element)));
    }

    private static SkillCategory ParseCategory(JsonElement element)
    {
        if (element.TryGetProperty(SkillCategoryJsonProperty, out var category) &&
            category.ValueKind == JsonValueKind.String &&
            Enum.TryParse<SkillCategory>(category.GetString(), true, out var parsed))
        {
            return parsed;
        }

        return SkillCategory.Action;
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

internal sealed record SeedSkill(string Name, SkillCategory Category);
