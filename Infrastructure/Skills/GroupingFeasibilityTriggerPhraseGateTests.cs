// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the inbox button of the grouping report in every language plugin: the phrase it sends
/// (groupingFeasibility.triggerPhrase) must contain one of that language's recipe synonyms of
/// prepare-groups-for-planning and must fire that recipe through the production RecipeTriggerMatcher
/// (synonym shortcut plus the recipe's own noneOf veto), otherwise the click would not start the recipe in
/// that language. That no earlier recipe hijacks the phrase is covered by RecipeSynonymRoutingTests, which
/// routes every pack synonym through the engine order. The four core languages are covered by
/// RecipeRoutingTests.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed.Models;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class GroupingFeasibilityTriggerPhraseGateTests
{
    private const string RecipeSlug = "prepare-groups-for-planning";
    private const string TriggerPhraseKey = "groupingFeasibility.triggerPhrase";
    private const string PluginLanguagesRelativePath = "Klacks.Api/Plugins/Languages";
    private const string DefinitionsRelativePath = "Klacks.Api/Application/Skills/Definitions";
    private const string RecipeSeedsFileName = "recipe-seeds.json";
    private const string TranslationsFileName = "translations.json";
    private const string RecipeSynonymsFileName = "recipe-synonyms.json";
    private const int ExpectedPluginLanguages = 21;

    private static readonly JsonSerializerOptions JsonReadOptions = new() { PropertyNameCaseInsensitive = true };

    [Test]
    public void TriggerPhrase_ContainsARecipeSynonym_AndFiresTheRecipe_InEveryLanguagePlugin()
    {
        var root = FindDirectory(PluginLanguagesRelativePath);
        root.ShouldNotBeNull();
        var definitions = FindDirectory(DefinitionsRelativePath);
        definitions.ShouldNotBeNull();

        var seed = JsonSerializer.Deserialize<RecipeSeedFile>(
            File.ReadAllText(Path.Combine(definitions, RecipeSeedsFileName)), JsonReadOptions);
        var trigger = seed!.Recipes.Single(recipe => recipe.Name == RecipeSlug).Trigger;

        var checkedLanguages = 0;
        var problems = new List<string>();
        foreach (var directory in Directory.GetDirectories(root))
        {
            var translations = Path.Combine(directory, TranslationsFileName);
            var synonymsFile = Path.Combine(directory, RecipeSynonymsFileName);
            if (!File.Exists(translations) || !File.Exists(synonymsFile))
            {
                continue;
            }

            checkedLanguages++;
            var language = Path.GetFileName(directory);
            using var translationDocument = JsonDocument.Parse(File.ReadAllText(translations));
            using var synonymDocument = JsonDocument.Parse(File.ReadAllText(synonymsFile));
            var phrase = translationDocument.RootElement.TryGetProperty(TriggerPhraseKey, out var value)
                ? value.GetString()
                : null;
            var synonyms = synonymDocument.RootElement.TryGetProperty(RecipeSlug, out var list)
                ? list.EnumerateArray().Select(item => item.GetString()!).ToList()
                : [];

            if (string.IsNullOrWhiteSpace(phrase)
                || !synonyms.Any(synonym => phrase.Contains(synonym, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"{language}: '{phrase}' contains none of [{string.Join(", ", synonyms)}]");
                continue;
            }

            if (!RecipeTriggerMatcher.Matches(trigger, synonyms, phrase, null, language, null))
            {
                problems.Add($"{language}: '{phrase}' does not fire {RecipeSlug} through the matcher");
            }
        }

        checkedLanguages.ShouldBe(ExpectedPluginLanguages);
        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    private static string? FindDirectory(string relativePath)
    {
        var segments = relativePath.Split('/');
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
