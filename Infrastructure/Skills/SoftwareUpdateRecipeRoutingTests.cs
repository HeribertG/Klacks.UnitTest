// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The questions and requests about installing a Klacks update belong to evaluate_software_update and
/// start_software_update. No guided recipe may take them: a recipe trigger runs before function calling and would leave a
/// pending confirmation behind. The sentences run against the real matcher and against every seeded recipe, because
/// several recipe triggers listen to words like "aktualisier", "update" or "aggiorna".
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SoftwareUpdateRecipeRoutingTests
{
    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions", "recipe-seeds.json"
    ];

    [TestCase("Soll ich jetzt updaten?", "de")]
    [TestCase("Ist jetzt ein guter Zeitpunkt für das Update?", "de")]
    [TestCase("Spricht etwas gegen ein Update?", "de")]
    [TestCase("Spiel das Update jetzt ein", "de")]
    [TestCase("Aktualisiere Klacks auf die neue Version", "de")]
    [TestCase("Starte das Software-Update", "de")]
    [TestCase("Should I update now?", "en")]
    [TestCase("Is now a good time to update?", "en")]
    [TestCase("Install the update now", "en")]
    [TestCase("Update the installation now", "en")]
    [TestCase("Upgrade Klacks to the new version", "en")]
    [TestCase("Dois-je mettre à jour maintenant ?", "fr")]
    [TestCase("Installe la mise à jour maintenant", "fr")]
    [TestCase("Mets Klacks à jour vers la nouvelle version", "fr")]
    [TestCase("Devo aggiornare adesso?", "it")]
    [TestCase("Installa l'aggiornamento adesso", "it")]
    [TestCase("Aggiorna Klacks alla nuova versione", "it")]
    public void UpdateSentence_StartsNoRecipe(string message, string language)
    {
        var hijackers = AllRecipes()
            .Where(recipe => RecipeTriggerMatcher.Matches(recipe.Trigger, null, message, language))
            .Select(recipe => recipe.Name)
            .ToList();

        hijackers.ShouldBeEmpty();
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
