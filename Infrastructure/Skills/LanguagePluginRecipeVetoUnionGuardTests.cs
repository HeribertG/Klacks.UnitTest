// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guard for the symmetric veto scope of the semantic recipe fallback. Since the semantic path vetoes with
/// the union of the pack vetoes of every installed language (AgentRecipe.AllVetoTerms) instead of only the
/// UI-language pack, a veto word of one language now also meets messages written in every other language.
/// This proves the union does not veto a legitimate positive: with all 21 recipe-vetoes.json files united
/// per recipe, no pack synonym sentence of that recipe (recipe-synonyms.json, 21 languages), no core goal
/// and goal translation (recipe-seeds.json) and no turn-selection goldset item that expects the recipe is
/// vetoed. The check runs through the production functions (AllVetoTerms, RecipeTriggerMatcher.IsVetoed),
/// not a re-implementation. The language list is explicit so a deleted pack fails instead of passing
/// vacantly. The knowledge-index recipe goldset (high/grey cases) lives in Klacks.IntegrationTest, which
/// no unit test reaches, so it is not part of this corpus.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed.Models;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class LanguagePluginRecipeVetoUnionGuardTests
{
    private const string RecipeSeedsFileName = "recipe-seeds.json";
    private const string RecipeVetoesFileName = "recipe-vetoes.json";
    private const string RecipeSynonymsFileName = "recipe-synonyms.json";
    private const string TurnGoldsetFileName = "turn-selection-v1.json";
    private const string GoldsetItemsProperty = "items";
    private const string GoldsetMessageProperty = "message";
    private const string GoldsetLocaleProperty = "locale";
    private const string GoldsetExpectedRecipeProperty = "expectedRecipe";
    private const string SeedGoalSource = "goal";

    private static readonly string[] PackLanguages =
    [
        "ar", "cs", "da", "el", "es", "fi", "he", "id", "ja", "ko", "ms",
        "nb", "nl", "pl", "pt", "ro", "sv", "th", "vi", "zh-CN", "zh-TW"
    ];

    private static readonly string[] DefinitionsRelativePath = ["Klacks.Api", "Application", "Skills", "Definitions"];

    private static readonly string[] GoldsetsRelativePath = ["Klacks.Api", "Application", "Skills", "Goldsets"];

    private static readonly string[] PluginsLanguagesRelativePath = ["Klacks.Api", "Plugins", "Languages"];

    private static readonly JsonSerializerOptions JsonReadOptions = new() { PropertyNameCaseInsensitive = true };

    [Test]
    public void UnitedPackVetoes_VetoNoLegitimatePositiveOfTheirRecipe()
    {
        var languagesDirectory = LocateDirectory(PluginsLanguagesRelativePath);
        var vetoesByRecipe = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);
        var positives = new List<(string Recipe, string Source, string Text)>();

        foreach (var language in PackLanguages)
        {
            var vetoes = ReadVocabulary(Path.Combine(languagesDirectory, language, RecipeVetoesFileName));
            vetoes.ShouldNotBeEmpty($"{language}/{RecipeVetoesFileName} is missing or empty");
            foreach (var (recipe, terms) in vetoes)
            {
                if (!vetoesByRecipe.TryGetValue(recipe, out var byLanguage))
                {
                    byLanguage = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    vetoesByRecipe[recipe] = byLanguage;
                }

                byLanguage[language] = terms;
            }

            var synonyms = ReadVocabulary(Path.Combine(languagesDirectory, language, RecipeSynonymsFileName));
            var sentences = synonyms.SelectMany(entry => entry.Value.Select(text => (entry.Key, language, text))).ToList();
            sentences.ShouldNotBeEmpty($"{language}/{RecipeSynonymsFileName} carries no sentence; the guard would pass vacantly");
            positives.AddRange(sentences);
        }

        positives.AddRange(LoadSeedGoals());
        positives.AddRange(LoadGoldsetRecipeItems());

        var unitedByRecipe = vetoesByRecipe.ToDictionary(
            entry => entry.Key,
            entry => new AgentRecipe { Name = entry.Key, Vetoes = entry.Value }.AllVetoTerms(),
            StringComparer.Ordinal);
        unitedByRecipe.ShouldNotBeEmpty();
        unitedByRecipe.Values.ShouldAllBe(terms => terms.Count > 0);

        var checkedPositives = 0;
        var collisions = new List<string>();
        foreach (var (recipe, source, text) in positives)
        {
            if (!unitedByRecipe.TryGetValue(recipe, out var united))
            {
                continue;
            }

            checkedPositives++;
            if (RecipeTriggerMatcher.IsVetoed(null, text, null, null, united))
            {
                var culprits = united.Where(term => RecipeTriggerMatcher.IsVetoed(null, text, null, null, [term]));
                var owners = culprits.Select(term => $"'{term}' ({string.Join(",", OwningLanguages(vetoesByRecipe[recipe], term))})");
                collisions.Add($"{recipe} [{source}] \"{text}\" vetoed by {string.Join(" / ", owners)}");
            }
        }

        TestContext.Progress.WriteLine(
            $"veto union guard: {unitedByRecipe.Count} vetoed recipes, {checkedPositives} positives checked, {collisions.Count} collisions");
        checkedPositives.ShouldBeGreaterThan(0);
        collisions.ShouldBeEmpty(
            "a veto word of one pack language vetoes a legitimate positive of the same recipe once the semantic "
            + "path unites all packs. Fix the pack word (or the sentence), do not drop the union: "
            + string.Join("; ", collisions));
    }

    private static IEnumerable<string> OwningLanguages(Dictionary<string, List<string>> byLanguage, string term) =>
        byLanguage
            .Where(entry => entry.Value.Contains(term, StringComparer.OrdinalIgnoreCase))
            .Select(entry => entry.Key);

    private static IEnumerable<(string Recipe, string Source, string Text)> LoadSeedGoals()
    {
        var seed = JsonSerializer.Deserialize<RecipeSeedFile>(
            File.ReadAllText(Path.Combine(LocateDirectory(DefinitionsRelativePath), RecipeSeedsFileName)), JsonReadOptions)!;

        foreach (var recipe in seed.Recipes)
        {
            yield return (recipe.Name, SeedGoalSource, recipe.Goal);
            foreach (var (language, goal) in recipe.GoalTranslations ?? new Dictionary<string, string>())
            {
                yield return (recipe.Name, $"{SeedGoalSource}:{language}", goal);
            }
        }
    }

    private static IEnumerable<(string Recipe, string Source, string Text)> LoadGoldsetRecipeItems()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(LocateDirectory(GoldsetsRelativePath), TurnGoldsetFileName)));
        var items = new List<(string, string, string)>();
        foreach (var item in document.RootElement.GetProperty(GoldsetItemsProperty).EnumerateArray())
        {
            if (!item.TryGetProperty(GoldsetExpectedRecipeProperty, out var recipe)
                || recipe.ValueKind != JsonValueKind.String
                || !item.TryGetProperty(GoldsetMessageProperty, out var message))
            {
                continue;
            }

            var locale = item.TryGetProperty(GoldsetLocaleProperty, out var value) ? value.GetString() : null;
            items.Add((recipe.GetString()!, $"goldset:{locale}", message.GetString() ?? string.Empty));
        }

        return items;
    }

    private static Dictionary<string, List<string>> ReadVocabulary(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path), JsonReadOptions)
              ?? new Dictionary<string, List<string>>()
            : new Dictionary<string, List<string>>();

    private static string LocateDirectory(string[] relativePath)
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

        throw new DirectoryNotFoundException(
            $"Could not locate {string.Join('/', relativePath)} from {AppContext.BaseDirectory}");
    }
}
