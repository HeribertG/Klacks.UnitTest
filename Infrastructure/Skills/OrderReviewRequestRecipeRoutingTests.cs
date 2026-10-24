// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The request the welcome focus "orders but no shifts" and the open-order goal send (i18n key
/// klacksy.orders.review.request) must start the guided order review deterministically: the recipe review-open-orders
/// matches the request in every language - the four core texts of Klacks.Ui by its structured trigger, every language
/// pack's text (translations.json) by that pack's recipe synonym - and no other recipe matches it, so the turn never
/// depends on the model guessing the intent. The recipe's only step is start_order_review, which seals nothing.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class OrderReviewRequestRecipeRoutingTests
{
    private const string RecipeName = "review-open-orders";
    private const string RequestKey = "klacksy.orders.review.request";

    private static readonly string[] RecipeSeedsPath =
        ["Klacks.Api", "Application", "Skills", "Definitions", "recipe-seeds.json"];

    private static readonly string[] LanguagesPath = ["Klacks.Api", "Plugins", "Languages"];

    private static readonly Dictionary<string, string> CoreRequests = new()
    {
        ["de"] = "Geh die offenen Bestellungen mit mir durch, bevor etwas versiegelt wird.",
        ["en"] = "Go through the open orders with me before anything is sealed.",
        ["fr"] = "Passe en revue les commandes ouvertes avec moi avant de sceller quoi que ce soit.",
        ["it"] = "Esamina con me gli ordini aperti prima di sigillare qualsiasi cosa.",
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IEnumerable<string> PackLanguages() =>
        Directory.GetDirectories(RepositoryRootLocator.FindDirectory(LanguagesPath)!)
            .Select(directory => new DirectoryInfo(directory).Name)
            .OrderBy(name => name, StringComparer.Ordinal);

    [TestCase("de")]
    [TestCase("en")]
    [TestCase("fr")]
    [TestCase("it")]
    public void CoreRequest_StartsTheOrderReview_AndNoOtherRecipe(string language)
    {
        AssertOnlyTheReviewMatches(CoreRequests[language], language, synonymsByRecipe: null);
    }

    [TestCaseSource(nameof(PackLanguages))]
    public void PackRequest_StartsTheOrderReview_AndNoOtherRecipe(string language)
    {
        var directory = Path.Combine(RepositoryRootLocator.FindDirectory(LanguagesPath)!, language);
        var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(directory, "translations.json")))!;
        var synonyms = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
            File.ReadAllText(Path.Combine(directory, "recipe-synonyms.json")))!;

        translations.ShouldContainKey(RequestKey);
        AssertOnlyTheReviewMatches(translations[RequestKey], language, synonyms);
    }

    [Test]
    public void TheRecipe_OnlyStartsTheReview()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryRootLocator.FindFile(RecipeSeedsPath)!));
        var recipe = document.RootElement.GetProperty("recipes").EnumerateArray()
            .Single(element => element.GetProperty("name").GetString() == RecipeName);

        var steps = recipe.GetProperty("steps").EnumerateArray().ToList();
        steps.Count.ShouldBe(1);
        steps[0].GetProperty("skill").GetString().ShouldBe("start_order_review");
    }

    private static void AssertOnlyTheReviewMatches(
        string request, string language, IReadOnlyDictionary<string, List<string>>? synonymsByRecipe)
    {
        var matched = LoadTriggers()
            .Where(recipe => RecipeTriggerMatcher.Matches(
                recipe.Trigger,
                synonymsByRecipe != null && synonymsByRecipe.TryGetValue(recipe.Name, out var synonyms) ? synonyms : null,
                request,
                language))
            .Select(recipe => recipe.Name)
            .ToList();

        matched.ShouldBe([RecipeName], $"'{request}' ({language}) matched: {string.Join(", ", matched)}");
    }

    private static List<(string Name, RecipeTrigger Trigger)> LoadTriggers()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryRootLocator.FindFile(RecipeSeedsPath)!));
        return document.RootElement.GetProperty("recipes").EnumerateArray()
            .Select(element => (
                element.GetProperty("name").GetString()!,
                JsonSerializer.Deserialize<RecipeTrigger>(element.GetProperty("trigger").GetRawText(), JsonOptions)!))
            .ToList();
    }
}
