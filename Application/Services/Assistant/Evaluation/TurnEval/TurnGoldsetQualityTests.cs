// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class TurnGoldsetQualityTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string RecipesSeedFileName = "recipe-seeds.json";
    private const string ClientEntityType = "client";
    private const string HonestyGoldsetFileName = "turn-honesty-v1.json";
    private const string HonestyModeMustAbstain = "must-abstain";

    // W0.5: lower bounds for the goldset build-out. The recipe and messaging targets are exact
    // (every recipe, every messaging plugin skill), the mutating-skill target is a floor.
    private const int MinTotalItemsAcrossGoldsets = 200;
    private const double MinMutatingSkillCoverage = 0.5;
    private const string MutatingEffect = "Mutate";

    private static readonly string[] GoldsetFileNames = ["turn-selection-v1.json", "turn-names-v1.json", HonestyGoldsetFileName];

    private static readonly Dictionary<string, (int Version, string Kind)> ExpectedVersionAndKind = new(StringComparer.Ordinal)
    {
        ["turn-selection-v1.json"] = (2, "turn-selection"),
        ["turn-names-v1.json"] = (2, "turn-selection"),
        [HonestyGoldsetFileName] = (1, "turn-honesty")
    };

    private static readonly string[] GoldsetsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Goldsets"
    ];

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] PluginsFeaturesRelativePath =
    [
        "Klacks.Api", "Plugins", "Features"
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Test]
    public void Goldsets_MustDeclareVersionAndKind()
    {
        foreach (var (fileName, document) in LoadGoldsets())
        {
            var expected = ExpectedVersionAndKind[fileName];
            document.Version.ShouldBe(expected.Version, fileName);
            document.Kind.ShouldBe(expected.Kind, fileName);
            document.Items.ShouldNotBeEmpty(fileName);
        }
    }

    [Test]
    public void HonestyGoldset_ItemsMustBeNoToolMustAbstainWithLocale()
    {
        var violations = new List<string>();

        foreach (var (fileName, document) in LoadGoldsets())
        {
            var isHonestyFile = string.Equals(fileName, HonestyGoldsetFileName, StringComparison.Ordinal);

            foreach (var item in document.Items)
            {
                if (isHonestyFile && item.Honesty == null)
                {
                    violations.Add($"{fileName}/{item.Id}: honesty item without honesty block");
                }

                if (item.Honesty == null)
                {
                    continue;
                }

                if (item.ExpectedTool != null)
                {
                    violations.Add($"{fileName}/{item.Id}: honesty item must not expect a tool call");
                }

                if (!string.Equals(item.Honesty.Mode, HonestyModeMustAbstain, StringComparison.Ordinal))
                {
                    violations.Add($"{fileName}/{item.Id}: unknown honesty mode '{item.Honesty.Mode}'");
                }

                if (string.IsNullOrWhiteSpace(item.Locale))
                {
                    violations.Add($"{fileName}/{item.Id}: honesty item must declare a locale");
                }
            }
        }

        violations.ShouldBeEmpty();
    }

    [Test]
    public void Goldsets_ItemIdsMustBeUnique()
    {
        var violations = new List<string>();

        foreach (var (fileName, document) in LoadGoldsets())
        {
            var duplicates = document.Items
                .GroupBy(i => i.Id, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => $"{fileName}: duplicate item id '{g.Key}'");
            violations.AddRange(duplicates);

            violations.AddRange(document.Items
                .Where(i => string.IsNullOrWhiteSpace(i.Id))
                .Select(_ => $"{fileName}: item with blank id"));
        }

        violations.ShouldBeEmpty();
    }

    [Test]
    public void Goldsets_ExpectedToolsAndAlternativesMustExistInSkillSeeds()
    {
        var skills = LoadSkillParameters();
        var violations = new List<string>();

        foreach (var (fileName, document) in LoadGoldsets())
        {
            foreach (var item in document.Items.Where(i => i.ExpectedTool != null))
            {
                if (!skills.ContainsKey(item.ExpectedTool!))
                {
                    violations.Add($"{fileName}/{item.Id}: expectedTool '{item.ExpectedTool}' not found in {SkillSeedsFileName}");
                }

                violations.AddRange(item.AlternativeTools
                    .Where(alt => !skills.ContainsKey(alt))
                    .Select(alt => $"{fileName}/{item.Id}: alternativeTool '{alt}' not found in {SkillSeedsFileName}"));
            }
        }

        violations.ShouldBeEmpty();
    }

    [Test]
    public void Goldsets_ExpectedSlotsMustBeParametersOfTheSkill()
    {
        var skills = LoadSkillParameters();
        var violations = new List<string>();

        foreach (var (fileName, document) in LoadGoldsets())
        {
            foreach (var item in document.Items.Where(i => i.ExpectedTool != null && skills.ContainsKey(i.ExpectedTool!)))
            {
                var parameters = skills[item.ExpectedTool!];
                violations.AddRange(item.ExpectedSlots
                    .Where(slot => !parameters.Contains(slot.Name))
                    .Select(slot =>
                        $"{fileName}/{item.Id}: slot '{slot.Name}' is not a parameter of skill '{item.ExpectedTool}' " +
                        $"(parameters: {string.Join(", ", parameters.Order())})"));
            }
        }

        violations.ShouldBeEmpty();
    }

    [Test]
    public void Goldsets_NoToolItemsMustNotHaveSlots()
    {
        var violations = new List<string>();

        foreach (var (fileName, document) in LoadGoldsets())
        {
            violations.AddRange(document.Items
                .Where(i => i.ExpectedTool == null && i.ExpectedSlots.Count > 0)
                .Select(i => $"{fileName}/{i.Id}: no-tool item must not define expectedSlots"));
        }

        violations.ShouldBeEmpty();
    }

    [Test]
    public void Goldsets_ResolvedEntitySlotsMustReferenceEntityWithPositiveIdNumber()
    {
        var violations = new List<string>();

        foreach (var (fileName, document) in LoadGoldsets())
        {
            foreach (var item in document.Items)
            {
                foreach (var slot in item.ExpectedSlots.Where(s => s.Match == SlotMatchMode.ResolvedEntityId))
                {
                    if (slot.Entity == null)
                    {
                        violations.Add($"{fileName}/{item.Id}: resolved-entity-id slot '{slot.Name}' has no entity reference");
                    }
                    else if (slot.Entity.IdNumber <= 0)
                    {
                        violations.Add($"{fileName}/{item.Id}: resolved-entity-id slot '{slot.Name}' has non-positive idNumber {slot.Entity.IdNumber}");
                    }
                    else if (string.IsNullOrWhiteSpace(slot.Entity.Type))
                    {
                        violations.Add($"{fileName}/{item.Id}: resolved-entity-id slot '{slot.Name}' has blank entity type");
                    }
                    else if (!string.Equals(slot.Entity.Type, ClientEntityType, StringComparison.OrdinalIgnoreCase))
                    {
                        violations.Add($"{fileName}/{item.Id}: resolved-entity-id slot '{slot.Name}' uses unsupported entity type '{slot.Entity.Type}'");
                    }
                }
            }
        }

        violations.ShouldBeEmpty();
    }

    // W0.5: the build-out target is expressed as testable lower bounds, not just a plan number.
    [Test]
    public void Goldsets_MeetW05CoverageTargets()
    {
        var documents = LoadGoldsets();
        var items = documents.SelectMany(d => d.Document.Items).ToList();
        var expectedTools = items
            .Where(i => i.ExpectedTool != null)
            .Select(i => i.ExpectedTool!)
            .ToHashSet(StringComparer.Ordinal);
        var expectedRecipes = items
            .Where(i => i.ExpectedRecipe != null)
            .Select(i => i.ExpectedRecipe!)
            .ToHashSet(StringComparer.Ordinal);

        var violations = new List<string>();

        if (items.Count < MinTotalItemsAcrossGoldsets)
        {
            violations.Add($"goldset total {items.Count} below {MinTotalItemsAcrossGoldsets}");
        }

        // 25/25 recipes must appear as expectedRecipe items.
        foreach (var recipeName in LoadRecipeNames())
        {
            if (!expectedRecipes.Contains(recipeName))
            {
                violations.Add($"recipe '{recipeName}' has no expectedRecipe item");
            }
        }

        // 3/3 messaging plugin skills must appear as expectedTool items.
        foreach (var messagingSkill in LoadMessagingSkillNames())
        {
            if (!expectedTools.Contains(messagingSkill))
            {
                violations.Add($"messaging skill '{messagingSkill}' has no expectedTool item");
            }
        }

        // >= 50 % of mutating skills must be covered by at least one expectedTool item.
        var mutatingSkills = LoadSkillEffects()[MutatingEffect];
        var coveredMutating = mutatingSkills.Count(expectedTools.Contains);
        var coverage = (double)coveredMutating / mutatingSkills.Count;
        if (coverage < MinMutatingSkillCoverage)
        {
            violations.Add(
                $"mutating-skill coverage {coverage:P1} ({coveredMutating}/{mutatingSkills.Count}) below {MinMutatingSkillCoverage:P0}");
        }

        violations.ShouldBeEmpty();
    }

    private static List<(string FileName, TurnGoldsetDocument Document)> LoadGoldsets()
    {
        return GoldsetFileNames
            .Select(fileName =>
            {
                var path = LocateRepoFile(GoldsetsRelativePath, fileName);
                var document = JsonSerializer.Deserialize<TurnGoldsetDocument>(File.ReadAllText(path), SerializerOptions);
                document.ShouldNotBeNull(fileName);
                return (fileName, document!);
            })
            .ToList();
    }

    private static Dictionary<string, HashSet<string>> LoadSkillParameters()
    {
        var skills = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        AddParametersFromSeedFile(skills, LocateRepoFile(DefinitionsRelativePath, SkillSeedsFileName));

        // W0.5: plugin seeds (messaging send_message/read_messages/list_messaging_providers) live in
        // Plugins/Features/*/skill-seeds.json and were invisible to the quality gates before.
        foreach (var pluginSeedFile in EnumeratePluginSeedFiles())
        {
            AddParametersFromSeedFile(skills, pluginSeedFile);
        }

        return skills;
    }

    private static void AddParametersFromSeedFile(Dictionary<string, HashSet<string>> skills, string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        foreach (var skill in EnumerateSkills(document.RootElement))
        {
            var name = skill.GetProperty("name").GetString() ?? string.Empty;
            var parameters = new HashSet<string>(StringComparer.Ordinal);

            if (skill.TryGetProperty("parameters", out var parameterArray)
                && parameterArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var parameter in parameterArray.EnumerateArray())
                {
                    if (parameter.TryGetProperty("name", out var parameterName)
                        && parameterName.ValueKind == JsonValueKind.String)
                    {
                        parameters.Add(parameterName.GetString()!);
                    }
                }
            }

            skills[name] = parameters;
        }
    }

    private static IEnumerable<JsonElement> EnumerateSkills(JsonElement root)
    {
        // Core seeds are { "version": …, "skills": […] }, plugin seeds are a bare array.
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().ToList();
        }

        if (root.TryGetProperty("skills", out var skillsArray) && skillsArray.ValueKind == JsonValueKind.Array)
        {
            return skillsArray.EnumerateArray().ToList();
        }

        return [];
    }

    private static HashSet<string> LoadRecipeNames()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateRepoFile(DefinitionsRelativePath, RecipesSeedFileName)));
        return document.RootElement
            .GetProperty("recipes")
            .EnumerateArray()
            .Select(r => r.GetProperty("name").GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> LoadMessagingSkillNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pluginSeedFile in EnumeratePluginSeedFiles())
        {
            using var document = JsonDocument.Parse(File.ReadAllText(pluginSeedFile));
            foreach (var skill in EnumerateSkills(document.RootElement))
            {
                names.Add(skill.GetProperty("name").GetString() ?? string.Empty);
            }
        }

        return names;
    }

    private static Dictionary<string, HashSet<string>> LoadSkillEffects()
    {
        var effects = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        AddEffectsFromSeedFile(effects, LocateRepoFile(DefinitionsRelativePath, SkillSeedsFileName));
        foreach (var pluginSeedFile in EnumeratePluginSeedFiles())
        {
            AddEffectsFromSeedFile(effects, pluginSeedFile);
        }

        return effects;
    }

    private static void AddEffectsFromSeedFile(Dictionary<string, HashSet<string>> effects, string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        foreach (var skill in EnumerateSkills(document.RootElement))
        {
            var name = skill.GetProperty("name").GetString() ?? string.Empty;
            var effect = skill.TryGetProperty("effect", out var effectProperty) && effectProperty.ValueKind == JsonValueKind.String
                ? effectProperty.GetString() ?? string.Empty
                : string.Empty;

            if (!effects.TryGetValue(effect, out var names))
            {
                names = new HashSet<string>(StringComparer.Ordinal);
                effects[effect] = names;
            }

            names.Add(name);
        }
    }

    private static IEnumerable<string> EnumeratePluginSeedFiles()
    {
        var pluginsDir = LocateRepoDirectory(PluginsFeaturesRelativePath);
        return Directory.GetFiles(pluginsDir, SkillSeedsFileName, SearchOption.AllDirectories);
    }

    private static string LocateRepoDirectory(string[] relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine([dir.FullName, .. relativePath]);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate {string.Join('/', relativePath)} by walking up from the test base directory.");
    }

    private static string LocateRepoFile(string[] relativePath, string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine([dir.FullName, .. relativePath, fileName]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {string.Join('/', relativePath)}/{fileName} by walking up from the test base directory.");
    }
}
