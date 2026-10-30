// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The consequence questions that belong to evaluate_compliance_enforcement_change must not start any guided recipe: a
/// recipe trigger runs before function calling and would take the turn. SkillRecipeTriggerCrossQualityTests only sees
/// the skill's trigger keywords, so real questions are pinned here against the structured trigger of EVERY seeded recipe
/// with the real matcher. Not covered:
/// the pack synonyms of plugin languages (passed as null here) and the semantic fallback of the recipe engine.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class ComplianceEnforcementChangeRecipeRoutingTests
{
    private static readonly string[] RecipesRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions", "recipe-seeds.json"
    ];

    [TestCase("Was passiert, wenn ich die Ruhezeit auf Blockieren stelle?", "de")]
    [TestCase("Wie viele Warnungen würden zu Fehlern, wenn ich die Höchstarbeitszeit blockiere?", "de")]
    [TestCase("Was wäre, wenn ich Feiertagsarbeit künftig ablehne statt nur zu warnen?", "de")]
    [TestCase("Lehnt die Regel Ruhetage schon ab oder warnt sie nur?", "de")]
    [TestCase("Könnten Vorgesetzte eine abgelehnte Zuweisung trotzdem durchsetzen?", "de")]
    [TestCase("What happens if I set the rest time rule to block?", "en")]
    [TestCase("How many warnings would become errors if I block maximum weekly hours?", "en")]
    [TestCase("What would change if work on public holidays were refused?", "en")]
    [TestCase("Does the rest day rule already refuse or only warn?", "en")]
    [TestCase("Could supervisors still push a refused assignment through?", "en")]
    [TestCase("Que se passe-t-il si je règle le temps de repos sur refuser ?", "fr")]
    [TestCase("Combien d'avertissements deviendraient des erreurs si je bloque le maximum d'heures ?", "fr")]
    [TestCase("Cette règle refuse-t-elle déjà ou avertit-elle seulement ?", "fr")]
    [TestCase("Cosa succede se imposto il riposo su rifiuta?", "it")]
    [TestCase("Quanti avvisi diventerebbero errori se blocco le ore massime?", "it")]
    [TestCase("Questa regola rifiuta già o avvisa soltanto?", "it")]
    public void ConsequenceQuestion_StartsNoRecipe(string message, string language)
    {
        var started = Recipes()
            .Where(recipe => RecipeTriggerMatcher.Matches(recipe.Trigger, null, message, language))
            .Select(recipe => recipe.Name)
            .ToList();

        started.ShouldBeEmpty($"'{message}' would start: {string.Join(", ", started)}");
    }

    [Test]
    public void Control_AClearCloseRequest_DoesStartItsRecipe_SoTheQuestionsAboveCouldHaveFailed()
    {
        var started = Recipes()
            .Where(recipe => RecipeTriggerMatcher.Matches(recipe.Trigger, null, "Schliesse die Periode Mai ab", "de"))
            .Select(recipe => recipe.Name)
            .ToList();

        started.ShouldContain("close-payroll-period");
    }

    private static List<(string Name, RecipeTrigger Trigger)> Recipes()
    {
        var path = RepositoryRootLocator.RequireFile(RecipesRelativePath);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        return document.RootElement.GetProperty("recipes")
            .EnumerateArray()
            .Select(recipe => (
                recipe.GetProperty("name").GetString()!,
                recipe.GetProperty("trigger").Deserialize<RecipeTrigger>(options)!))
            .ToList();
    }
}
