// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The questions that belong to evaluate_spam_rule_candidates must not start any guided recipe: a recipe trigger runs
/// before function calling and would take the turn. SkillRecipeTriggerCrossQualityTests only sees the skill's trigger
/// keywords, so real questions are pinned here against the structured trigger of EVERY seeded recipe with the real
/// matcher. Not covered: the pack synonyms of plugin languages (passed as null here) and the semantic fallback of the
/// recipe engine.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SpamRuleCandidatesRecipeRoutingTests
{
    private static readonly string[] RecipesRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions", "recipe-seeds.json"
    ];

    [TestCase("Welche Absender-Domains landen immer wieder im Junk und haben noch keine Regel?", "de")]
    [TestCase("Welche Domains sollte ich als Spam einstufen lassen?", "de")]
    [TestCase("Gibt es Absender, die ständig im Junk-Ordner auftauchen?", "de")]
    [TestCase("Für welche Domains fehlt noch ein Spam-Filter?", "de")]
    [TestCase("Welche Domain schickt mir am meisten Junk?", "de")]
    [TestCase("Which sender domains keep landing in junk and have no rule yet?", "en")]
    [TestCase("Are there senders that keep showing up in the junk folder?", "en")]
    [TestCase("Which domains should I have filtered as spam?", "en")]
    [TestCase("Which domain sends me the most junk?", "en")]
    [TestCase("Quels domaines d'expéditeurs reviennent sans cesse dans les indésirables sans règle ?", "fr")]
    [TestCase("Quel domaine m'envoie le plus de courrier indésirable ?", "fr")]
    [TestCase("Quali domini mittenti finiscono sempre nella posta indesiderata senza una regola?", "it")]
    [TestCase("Quale dominio mi manda più posta indesiderata?", "it")]
    public void RecurringJunkSenderQuestion_StartsNoRecipe(string message, string language)
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
