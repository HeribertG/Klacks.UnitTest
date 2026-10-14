// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins that no item of the rights goldset is taken over by a deterministic recipe: neither the forcing resolver
/// nor any recipe keyword trigger (pack synonyms and pack vetoes of the item's language included) may fire on its
/// message, because a hijacked item is Excluded and measures nothing. The semantic recipe fallback is embedding
/// based and not covered here. Also reports which items the mutation-intent detector forces a tool call on, which
/// is what makes a plain refusal impossible in production for those items.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

[TestFixture]
public class TurnRightsGoldsetRecipeCollisionTests
{
    private static readonly string[] GoldsetPath = ["Klacks.Api", "Application", "Skills", "Goldsets", "turn-rights-v1.json"];
    private static readonly string[] RecipeSeedPath = ["Klacks.Api", "Application", "Skills", "Definitions", "recipe-seeds.json"];
    private static readonly string[] LanguagesPath = ["Klacks.Api", "Plugins", "Languages"];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Test]
    public void NoRightsItem_IsTakenOverByADeterministicRecipe()
    {
        var triggers = LoadTriggers();
        var synonymsByLocale = LoadPackFile("recipe-synonyms.json");
        var vetoesByLocale = LoadPackFile("recipe-vetoes.json");
        var hits = new List<string>();

        foreach (var item in LoadItems())
        {
            if (RecipeForcingResolver.Resolve(item.Message) != null)
            {
                hits.Add($"{item.Id}: forcing resolver fires");
            }

            foreach (var (recipe, trigger) in triggers)
            {
                var synonyms = ForLocale(synonymsByLocale, item.Locale!, recipe);
                var vetoes = ForLocale(vetoesByLocale, item.Locale!, recipe);
                if (RecipeTriggerMatcher.Matches(trigger, synonyms, item.Message, null, item.Locale, vetoes))
                {
                    hits.Add($"{item.Id}: recipe '{recipe}' triggers");
                }
            }
        }

        hits.ShouldBeEmpty(string.Join(Environment.NewLine, hits));
    }

    [Test]
    public void ReportMutationIntentItems()
    {
        foreach (var item in LoadItems())
        {
            TestContext.WriteLine($"{item.Id} mutationIntent={MutationIntentDetector.IsMutationIntent(item.Message)}");
        }
    }

    private static List<TurnGoldsetItem> LoadItems() =>
        JsonSerializer.Deserialize<TurnGoldsetDocument>(
            File.ReadAllText(RepositoryRootLocator.RequireFile(GoldsetPath)), JsonOptions)!.Items;

    private static Dictionary<string, RecipeTrigger> LoadTriggers()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryRootLocator.RequireFile(RecipeSeedPath)));
        var triggers = new Dictionary<string, RecipeTrigger>(StringComparer.Ordinal);
        foreach (var recipe in document.RootElement.GetProperty("recipes").EnumerateArray())
        {
            if (recipe.TryGetProperty("trigger", out var trigger) && trigger.ValueKind == JsonValueKind.Object)
            {
                triggers[recipe.GetProperty("name").GetString()!] =
                    JsonSerializer.Deserialize<RecipeTrigger>(trigger.GetRawText(), JsonOptions)!;
            }
        }

        return triggers;
    }

    private static Dictionary<string, Dictionary<string, List<string>>> LoadPackFile(string fileName)
    {
        var result = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.GetDirectories(RepositoryRootLocator.RequireDirectory(LanguagesPath)))
        {
            var file = Path.Combine(directory, fileName);
            if (File.Exists(file))
            {
                result[Path.GetFileName(directory)] =
                    JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(file))!;
            }
        }

        return result;
    }

    private static IReadOnlyCollection<string>? ForLocale(
        Dictionary<string, Dictionary<string, List<string>>> byLocale, string locale, string recipe) =>
        byLocale.TryGetValue(locale, out var byRecipe) && byRecipe.TryGetValue(recipe, out var terms) ? terms : null;
}
