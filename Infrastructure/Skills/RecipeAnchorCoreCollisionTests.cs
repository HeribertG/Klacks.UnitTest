// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Cost gate for the language-pack recipe anchors on core-language turns. The pack anchors of all 21
/// languages are evaluated against every message (the request language is the UI language, not the
/// message language), so a pack term that happens to occur in a German, English, French or Italian
/// sentence lets that sentence pass the semantic anchor of a recipe it never named. Measured on the
/// turn-selection-v1 skill turns (expectedTool set, no expectedRecipe, locale de/en/fr/it) against every
/// multi-condition seed recipe: a (message, recipe) pair is pack-only when the core trigger misses
/// (CountAnchors below MinRequiredAnchors) and HasSemanticAnchor with all 21 packs still passes. Their
/// number may not exceed MaxPackOnlyShareOfCorePairs of the pairs the core already lets through.
/// Baseline 2026-09-24 (hand-off simulation): 605 core pairs, 21 pack-only pairs, +1 message.
/// On failure the message lists every pack-only pair with the term and language that anchored it, plus a
/// term-level summary, so a collision is actionable. Do not raise the limit to make it pass - fix the term.
/// </summary>

using System.Text;
using System.Text.Json;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed.Models;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class RecipeAnchorCoreCollisionTests
{
    private const double MaxPackOnlyShareOfCorePairs = 0.05;
    private const int MultiConditionMinimum = 2;
    private const string RecipeSeedsFileName = "recipe-seeds.json";
    private const string RecipeAnchorsFileName = "recipe-anchors.json";
    private const string TurnGoldsetFileName = "turn-selection-v1.json";
    private const string GoldsetItemsProperty = "items";
    private const string GoldsetIdProperty = "id";
    private const string GoldsetMessageProperty = "message";
    private const string GoldsetLocaleProperty = "locale";
    private const string GoldsetExpectedToolProperty = "expectedTool";
    private const string GoldsetExpectedRecipeProperty = "expectedRecipe";

    private static readonly string[] CoreLocales = ["de", "en", "fr", "it"];

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
    public void PackOnlyAnchoredPairs_OnCoreSkillTurns_StayWithinTheShareOfCorePairs()
    {
        var recipes = LoadMultiConditionRecipes();
        var anchorsByLanguage = LoadAnchors();
        var turns = LoadCoreSkillTurns();

        recipes.ShouldNotBeEmpty();
        turns.ShouldNotBeEmpty();

        var corePairs = 0;
        var packOnly = new List<((string Id, string Locale, string Message) Turn, string Recipe, List<(string Term, string Language)> Culprits)>();
        var coreMessages = new HashSet<string>(StringComparer.Ordinal);
        var packOnlyMessages = new HashSet<string>(StringComparer.Ordinal);

        foreach (var turn in turns)
        {
            foreach (var recipe in recipes)
            {
                if (RecipeTriggerMatcher.CountAnchors(recipe.Trigger, turn.Message, language: turn.Locale)
                    >= RecipeTriggerMatcher.MinRequiredAnchors)
                {
                    corePairs++;
                    coreMessages.Add(turn.Id);
                    continue;
                }

                var packAnchors = PackAnchorsFor(anchorsByLanguage, recipe.Name);
                if (RecipeTriggerMatcher.HasSemanticAnchor(
                        recipe.Trigger, turn.Message, language: turn.Locale, packAnchors: packAnchors))
                {
                    packOnlyMessages.Add(turn.Id);
                    packOnly.Add((turn, recipe.Name, FindCulprits(recipe.Trigger, turn, packAnchors)));
                }
            }
        }

        var limit = corePairs * MaxPackOnlyShareOfCorePairs;
        var addedMessages = packOnlyMessages.Count(id => !coreMessages.Contains(id));
        TestContext.Progress.WriteLine(
            $"pack anchors on core skill turns: {turns.Count} messages, {recipes.Count} recipes, " +
            $"{corePairs} core pairs, {packOnly.Count} pack-only pairs (limit {limit:F1}), +{addedMessages} messages");

        corePairs.ShouldBeGreaterThan(0);
        if (packOnly.Count > limit)
        {
            Assert.Fail(BuildReport(packOnly, corePairs, limit));
        }
    }

    private static List<(string Term, string Language)> FindCulprits(
        RecipeTrigger trigger, (string Id, string Locale, string Message) turn, Dictionary<string, List<string>> packAnchors) =>
        packAnchors
            .SelectMany(pack => pack.Value.Select(term => (Term: term, Language: pack.Key)))
            .Where(candidate => RecipeTriggerMatcher.HasSemanticAnchor(
                trigger, turn.Message, language: turn.Locale,
                packAnchors: new Dictionary<string, List<string>> { [candidate.Language] = [candidate.Term] }))
            .ToList();

    private static string BuildReport(
        List<((string Id, string Locale, string Message) Turn, string Recipe, List<(string Term, string Language)> Culprits)> packOnly,
        int corePairs, double limit)
    {
        var report = new StringBuilder();
        report.AppendLine(
            $"{packOnly.Count} (message, recipe) pairs pass the semantic anchor only through a pack term, " +
            $"limit {limit:F1} = {MaxPackOnlyShareOfCorePairs:P0} of {corePairs} core pairs. Offending pairs:");
        foreach (var pair in packOnly)
        {
            report.AppendLine(
                $"  {pair.Recipe} <- {pair.Turn.Id} [{pair.Turn.Locale}] \"{pair.Turn.Message}\" via " +
                string.Join(", ", pair.Culprits.Select(c => $"'{c.Term}' ({c.Language})")));
        }

        report.AppendLine("Term-level summary (term, language: pairs / recipes):");
        foreach (var group in packOnly
                     .SelectMany(pair => pair.Culprits.Select(c => (c.Term, c.Language, pair.Recipe)))
                     .GroupBy(entry => (entry.Term, entry.Language))
                     .OrderByDescending(group => group.Count()))
        {
            report.AppendLine(
                $"  '{group.Key.Term}' ({group.Key.Language}): {group.Count()} / " +
                string.Join(", ", group.Select(entry => entry.Recipe).Distinct(StringComparer.Ordinal)));
        }

        return report.ToString();
    }

    private static Dictionary<string, List<string>> PackAnchorsFor(
        Dictionary<string, Dictionary<string, List<string>>> anchorsByLanguage, string recipeName) =>
        anchorsByLanguage
            .Where(pack => pack.Value.ContainsKey(recipeName))
            .ToDictionary(pack => pack.Key, pack => pack.Value[recipeName], StringComparer.Ordinal);

    private static List<RecipeSeedDefinition> LoadMultiConditionRecipes()
    {
        var seed = JsonSerializer.Deserialize<RecipeSeedFile>(
            File.ReadAllText(Path.Combine(LocateDirectory(DefinitionsRelativePath), RecipeSeedsFileName)), JsonReadOptions)!;
        return seed.Recipes.Where(r => r.Trigger.AllOf.Count >= MultiConditionMinimum).ToList();
    }

    private static Dictionary<string, Dictionary<string, List<string>>> LoadAnchors()
    {
        var directory = LocateDirectory(PluginsLanguagesRelativePath);
        var anchors = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);
        foreach (var language in PackLanguages)
        {
            var path = Path.Combine(directory, language, RecipeAnchorsFileName);
            File.Exists(path).ShouldBeTrue($"{language}/{RecipeAnchorsFileName} is missing");
            anchors[language] = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
                File.ReadAllText(path), JsonReadOptions)!;
        }

        return anchors;
    }

    private static List<(string Id, string Locale, string Message)> LoadCoreSkillTurns()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(LocateDirectory(GoldsetsRelativePath), TurnGoldsetFileName)));
        var turns = new List<(string Id, string Locale, string Message)>();
        foreach (var item in document.RootElement.GetProperty(GoldsetItemsProperty).EnumerateArray())
        {
            var locale = StringProperty(item, GoldsetLocaleProperty);
            var message = StringProperty(item, GoldsetMessageProperty);
            if (StringProperty(item, GoldsetExpectedToolProperty) == null
                || StringProperty(item, GoldsetExpectedRecipeProperty) != null
                || message == null
                || locale == null
                || !CoreLocales.Contains(locale, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            turns.Add((StringProperty(item, GoldsetIdProperty) ?? message, locale, message));
        }

        return turns;
    }

    private static string? StringProperty(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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
