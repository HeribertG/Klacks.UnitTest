// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Routing regression tests for the geographic-grouping recipes: loads the real recipe-seeds.json,
/// orders the recipes by sortOrder (the engine's resolution order) and asserts that the actual user
/// utterances that exposed the bug now resolve to bulk-add-employees-to-nearest-group, while the
/// criteria-based "add to one named group" phrasing still resolves to bulk-add-employees-to-group and
/// the customer phrasing still resolves to bulk-add-customers-to-nearest-group. This locks the
/// allOf/noneOf disjointness that RecipeSeedQualityTests does not cover.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed.Models;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeRoutingTests
{
    private const string RecipeSeedsFileName = "recipe-seeds.json";

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
        var file = LocateDefinitionsFile(RecipeSeedsFileName);
        var seed = JsonSerializer.Deserialize<RecipeSeedFile>(File.ReadAllText(file), JsonReadOptions);
        _recipes = seed!.Recipes.OrderBy(r => r.SortOrder).ToList();
    }

    private static string? Resolve(string message)
        => _recipes.FirstOrDefault(r => RecipeTriggerMatcher.Matches(r.Trigger, message))?.Name;

    [Test]
    [TestCase("Kannst du unsere Mitarbeiter auf die für sie besten Gruppen verteilen?")]
    [TestCase("Verteile alle Mitarbeiter auf die für sie besten Gruppen")]
    [TestCase("Ich will alle Mitarbeiter zu den Gruppen verteilen die für sie am besten passt")]
    [TestCase("Ordne jeden Mitarbeiter der nächstgelegenen Gruppe zu")]
    public void EmployeeBestGroupUtterances_RouteToNearestGroupRecipe(string utterance)
    {
        Assert.That(Resolve(utterance), Is.EqualTo("bulk-add-employees-to-nearest-group"));
    }

    [Test]
    [TestCase("Füge alle Mitarbeiter aus dem Kanton Bern zur Gruppe Zürich hinzu")]
    [TestCase("Verteile alle Mitarbeiter mit dem Vertrag Vollzeit auf die Gruppe Basel")]
    public void EmployeeCriteriaUtterances_RouteToCriteriaRecipe(string utterance)
    {
        Assert.That(Resolve(utterance), Is.EqualTo("bulk-add-employees-to-group"));
    }

    [Test]
    public void CustomerBestGroupUtterance_StillRoutesToCustomerRecipe()
    {
        Assert.That(
            Resolve("Verteile alle Kunden auf die für sie nächsten Gruppen"),
            Is.EqualTo("bulk-add-customers-to-nearest-group"));
    }

    [Test]
    [TestCase("Füge die markierten Mitarbeiter zur Gruppe Bern hinzu")]
    [TestCase("Ordne die selektierten Mitarbeiter der Gruppe Zürich zu")]
    [TestCase("Füge die ausgewählten Mitarbeiter zur Gruppe Bern hinzu")]
    [TestCase("Füge alle markierten Kunden zur Gruppe Basel hinzu")]
    public void SelectionUtterances_RouteToSelectedClientsRecipe(string utterance)
    {
        Assert.That(Resolve(utterance), Is.EqualTo("add-selected-clients-to-group"));
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

        throw new FileNotFoundException($"Could not locate {fileName} from {AppContext.BaseDirectory}");
    }
}
