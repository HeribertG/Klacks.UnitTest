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

    /// <summary>
    /// The utterances in this fixture are German unless a test case says otherwise, so German is the
    /// default rather than a value every call site repeats. It must stay an explicit parameter: since
    /// the bulk marker "alle" is bound to de through anyWordStartByLocale, resolving without a language
    /// is a different question than resolving as German, and conflating the two hid a regression.
    /// </summary>
    private const string German = "de";

    [OneTimeSetUp]
    public void LoadSeededRecipes()
    {
        var file = LocateDefinitionsFile(RecipeSeedsFileName);
        var seed = JsonSerializer.Deserialize<RecipeSeedFile>(File.ReadAllText(file), JsonReadOptions);
        _recipes = seed!.Recipes.OrderBy(r => r.SortOrder).ToList();
    }

    private static string? Resolve(string message, string? language = German)
        => _recipes.FirstOrDefault(r => RecipeTriggerMatcher.Matches(r.Trigger, null, message, language))?.Name;

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
    [TestCase("Bis wann muss ich die Pläne fertig haben, damit sie rechtzeitig per Post ankommen?", "de")]
    [TestCase("Klacksy sollte in Gespräch mit dem User herausfinden, wann die Planungen bis spätesten gemacht werden müssen damit sie rechtzeitig via Email oder Post versenden werden können.", "de")]
    [TestCase("Bis wann muss die Planung stehen, damit ich sie noch per E-Mail versenden kann?", "de")]
    [TestCase("By when do I have to finish the roster so it reaches everyone in time by post?", "en")]
    [TestCase("Jusqu'a quand dois-je terminer la planification pour qu'elle arrive a temps par courrier ?", "fr")]
    [TestCase("Entro quando devo finire la pianificazione perche arrivi in tempo per posta?", "it")]
    public void PlanDeliveryDeadlineUtterances_RouteToThePlanDeliveryDeadlineRecipe(string utterance, string language)
    {
        Assert.That(Resolve(utterance, language), Is.EqualTo("plan-delivery-deadline"));
    }

    [Test]
    [TestCase("Erstelle einen Dienstplan und versende ihn per E-Mail spätestens morgen")]
    [TestCase("Wie fange ich an?")]
    [TestCase("Wir haben abwesende Mitarbeiter im Briefing")]
    public void OtherUtterances_DoNotRouteToThePlanDeliveryDeadlineRecipe(string utterance)
    {
        Assert.That(Resolve(utterance), Is.Not.EqualTo("plan-delivery-deadline"));
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

    /// <summary>
    /// Pins the hazard the de-bound bulk marker creates, so it stays a known contract instead of a
    /// surprise. "alle" cannot be a language-neutral whole-word term: Italian "alle" (a+le, as in
    /// "dalle 7 alle 15") and Finnish "alle" are real words, so anyWordStartByLocale fires it only for
    /// a detected de. The price is that resolving WITHOUT a language loses the bulk marker - and it
    /// degrades to the single-add recipe, not to nothing, which is the dangerous direction: a request
    /// about every employee becomes a write about one. Both production entry points supply a language
    /// (ChatController from the request, SlackOwnerBridgeService from the installation default); any
    /// new one must too, and this test is what makes forgetting visible.
    /// </summary>
    [Test]
    public void LocaleBoundBulkMarker_DegradesToTheSingleRecipeWithoutADetectedLanguage()
    {
        const string utterance = "Füge alle Mitarbeiter aus dem Kanton Bern zur Gruppe Zürich hinzu";

        Assert.That(Resolve(utterance, German), Is.EqualTo("bulk-add-employees-to-group"));
        Assert.That(Resolve(utterance, null), Is.EqualTo("add-employee-to-group"));
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
