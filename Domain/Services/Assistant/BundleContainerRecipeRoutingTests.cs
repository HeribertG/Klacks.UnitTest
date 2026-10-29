// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Routing tests for the bundle-mobile-services-into-container recipe against the real recipe-seeds.json, ordered by
/// sortOrder like the engine: bundle requests that speak of "Einsätze", a container or a named group reach the bundle
/// recipe. Before, "bündle nur die Einsätze der Gruppe Spitex Winterthur" matched no keyword trigger: the imperative
/// "bündle" does not start with the stem "bündel", and "Einsätze" was no service term. Adding a shift to a group and
/// moving a group still reach their own recipes. The recipe asks for the group as the free-text slot groupFilter (an
/// answer like "alle" is valid, so it is no entity-name slot for the correction gate) and leaves its mapping to
/// groupName to the mutate note.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed.Models;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class BundleContainerRecipeRoutingTests
{
    private const string RecipeSeedsFileName = "recipe-seeds.json";
    private const string BundleRecipe = "bundle-mobile-services-into-container";
    private const string GroupSlot = "groupFilter";
    private const string AskKind = "ask";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static List<RecipeSeedDefinition> _recipes = null!;

    [OneTimeSetUp]
    public void LoadSeededRecipes()
    {
        var file = RepositoryRootLocator.RequireFile([.. DefinitionsRelativePath, RecipeSeedsFileName]);
        var seed = JsonSerializer.Deserialize<RecipeSeedFile>(File.ReadAllText(file), JsonReadOptions);
        _recipes = seed!.Recipes.OrderBy(r => r.SortOrder).ToList();
    }

    private static string? Resolve(string message, string language)
        => _recipes.FirstOrDefault(r => RecipeTriggerMatcher.Matches(r.Trigger, null, message, language))?.Name;

    [Test]
    [TestCase("Bündle nur die Einsätze der Spitex Winterthur am Donnerstag", "de")]
    [TestCase("Bündle die Einsätze der Gruppe Spitex Winterthur in einen Container", "de")]
    [TestCase("Bündle alle Einsätze rund um Winterthur in einen Container", "de")]
    [TestCase("Bündel die mobilen Dienste der Gruppe Spitex Winterthur", "de")]
    [TestCase("Bundle the services of the Spitex Winterthur group into a container", "en")]
    [TestCase("Regroupe les interventions du groupe Spitex Winterthur", "fr")]
    [TestCase("Raggruppa gli interventi del gruppo Spitex Winterthur", "it")]
    public void BundleRequests_WithEinsaetzeContainerOrGroup_RouteToTheBundleRecipe(string utterance, string language)
    {
        Assert.That(Resolve(utterance, language), Is.EqualTo(BundleRecipe));
    }

    [Test]
    [TestCase("Füge den Dienst Frühdienst zur Gruppe Spitex Winterthur hinzu", "de", "add-shift-to-group")]
    [TestCase("Verschiebe die Gruppe Spitex Winterthur unter Spitex Zürich", "de", "move-group")]
    public void GroupRequests_StillRouteToTheirOwnRecipes(string utterance, string language, string expected)
    {
        Assert.That(Resolve(utterance, language), Is.EqualTo(expected));
    }

    [Test]
    [TestCase("Wie bündle ich Einsätze in einen Container?", "de")]
    [TestCase("Füge eine Aufgabe zum Container hinzu", "de")]
    public void QuestionsAndContainerTaskRequests_DoNotRouteToTheBundleRecipe(string utterance, string language)
    {
        Assert.That(Resolve(utterance, language), Is.Not.EqualTo(BundleRecipe));
    }

    [Test]
    public void BundleRecipe_AsksForTheGroup_WithoutInjectingIt()
    {
        var recipe = _recipes.Single(r => r.Name == BundleRecipe);
        var steps = recipe.Steps.ToList();

        steps.Count(s => s.Kind == AskKind && s.Slot == GroupSlot).ShouldBe(1);
        steps.Where(s => s.Inject != null).SelectMany(s => s.Inject!.Keys).ShouldNotContain(GroupSlot);
    }
}
