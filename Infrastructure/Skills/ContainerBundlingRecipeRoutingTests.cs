// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Questions about whether a bundling is worthwhile belong to evaluate_container_bundling: no guided recipe may take them,
/// because a recipe trigger runs before function calling and would start asking for the slots of a write. The orders stay
/// with bundle-mobile-services-into-container, which is the positive control. The sentences run against the real matcher
/// and against every seeded recipe.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class ContainerBundlingRecipeRoutingTests
{
    private const string BundleRecipe = "bundle-mobile-services-into-container";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions", "recipe-seeds.json"
    ];

    [TestCase("Lohnt es sich, diese mobilen Dienste in einen Container zu bündeln?", "de")]
    [TestCase("Lohnt sich das Bündeln der mobilen Dienste rund um Winterthur?", "de")]
    [TestCase("Soll ich die mobilen Dienste in Winterthur bündeln?", "de")]
    [TestCase("Passen diese Einsätze in einen Container?", "de")]
    [TestCase("Passen die mobilen Dienste am Donnerstag zusammengefasst in eine Tour?", "de")]
    [TestCase("Würde es sich lohnen, die Einsätze in einem Container zu bündeln?", "de")]
    [TestCase("Wäre es sinnvoll, die mobilen Dienste zu bündeln?", "de")]
    [TestCase("Is it worth bundling these mobile services into a container?", "en")]
    [TestCase("Should I bundle these mobile services into a container?", "en")]
    [TestCase("Should we combine the mobile services around Winterthur?", "en")]
    [TestCase("Would it make sense to bundle these mobile services?", "en")]
    [TestCase("Do these mobile services fit if I bundle them?", "en")]
    [TestCase("Vaut-il la peine de regrouper ces services mobiles dans un conteneur ?", "fr")]
    [TestCase("Dois-je regrouper ces services mobiles ?", "fr")]
    [TestCase("Faut-il regrouper ces interventions dans un conteneur ?", "fr")]
    [TestCase("Conviene raggruppare questi servizi mobili in un contenitore?", "it")]
    [TestCase("Vale la pena raggruppare questi interventi?", "it")]
    [TestCase("Devo raggruppare questi servizi mobili?", "it")]
    public void AdviceQuestion_StartsNoRecipe(string message, string language)
    {
        var hijackers = AllRecipes()
            .Where(recipe => RecipeTriggerMatcher.Matches(recipe.Trigger, null, message, language))
            .Select(recipe => recipe.Name)
            .ToList();

        hijackers.ShouldBeEmpty();
    }

    [TestCase("Bündle die mobilen Dienste rund um Winterthur in einen Container", "de")]
    [TestCase("Mobile Dienste rund um Basel zusammenfassen", "de")]
    [TestCase("Bundle the mobile services around Winterthur", "en")]
    [TestCase("Regroupe les services mobiles autour de Winterthour", "fr")]
    [TestCase("Raggruppa i servizi mobili attorno a Winterthur", "it")]
    public void BundleOrder_StillStartsTheRecipe(string message, string language)
    {
        var recipe = AllRecipes().Single(candidate => candidate.Name == BundleRecipe);

        RecipeTriggerMatcher.Matches(recipe.Trigger, null, message, language).ShouldBeTrue();
    }

    private static List<(string Name, RecipeTrigger Trigger)> AllRecipes()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryRootLocator.RequireFile(DefinitionsRelativePath)));

        return document.RootElement.GetProperty("recipes")
            .EnumerateArray()
            .Where(recipe => recipe.TryGetProperty("trigger", out var trigger) && trigger.ValueKind == JsonValueKind.Object)
            .Select(recipe => (
                recipe.GetProperty("name").GetString()!,
                recipe.GetProperty("trigger").Deserialize<RecipeTrigger>(options)!))
            .ToList();
    }
}
