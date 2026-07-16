// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Routing regression tests for the geographic-grouping recipes: loads the real recipe-seeds.json,
/// orders the recipes by sortOrder (the engine's resolution order) and asserts that the actual user
/// utterances that exposed the bug now resolve to bulk-add-employees-to-nearest-group, while the
/// criteria-based "add to one named group" phrasing still resolves to bulk-add-employees-to-group and
/// the customer phrasing still resolves to bulk-add-customers-to-nearest-group. This locks the
/// allOf/noneOf disjointness that RecipeSeedQualityTests does not cover. Also locks that company-rule
/// intake utterances (start_company_rule territory) never engage any recipe: "neue Firmenregel: max. 3
/// Nachtschichten" used to fire create-shift-order via anyWordStart "neu" + anySubstring "schicht" and
/// hijacked the turn into the order-name ask step instead of the start_company_rule skill.
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
    [TestCase("Klacksy, wir haben eine neue Firmenregel: maximal 3 Nachtschichten pro Woche, hart blockieren.")]
    [TestCase("Neue Firmenregel: Nachtzuschlag 25% ab 23 Uhr")]
    [TestCase("Neue Hausregel: kein Dienst länger als 10 Stunden, blockieren")]
    [TestCase("Bitte lege eine neue Firmenregel an: maximal 2 Wochenendschichten pro Monat")]
    [TestCase("Wir haben eine neue Regel: maximal 3 Nachtschichten pro Woche")]
    [TestCase("New company rule: max 3 night shifts per week, block hard")]
    [TestCase("Neue Betriebsregel: Sonntagsdienste nur mit Zustimmung, bitte nur warnen")]
    public void CompanyRuleIntakeUtterances_DoNotEngageAnyRecipe(string utterance)
    {
        Assert.That(Resolve(utterance), Is.Null);
    }

    [Test]
    [TestCase("Erstelle eine neue Bestellung für den Kunden Migros")]
    [TestCase("Lege einen neuen Dienst an")]
    public void ShiftOrderUtterances_StillRouteToCreateShiftOrderRecipe(string utterance)
    {
        Assert.That(Resolve(utterance), Is.EqualTo("create-shift-order"));
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
    [TestCase("Füge diese Mitarbeiter zur Gruppe Bern hinzu")]
    [TestCase("Füge diese Mitarbeitenden zur Gruppe Bern hinzu")]
    [TestCase("Füge diese Kunden zur Gruppe Zürich hinzu")]
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
