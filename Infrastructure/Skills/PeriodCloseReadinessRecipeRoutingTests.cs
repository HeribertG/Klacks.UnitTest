// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The question "may I close this period yet" belongs to evaluate_period_close_readiness and must not start the guided
/// seal recipe close-payroll-period, whose trigger runs before function calling and leaves a pending confirmation
/// behind. SkillRecipeTriggerCrossQualityTests only sees the skill's trigger keywords, so these sentences are pinned
/// against the real matcher here, together with the requests that must keep starting the recipe.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class PeriodCloseReadinessRecipeRoutingTests
{
    private const string CloseRecipe = "close-payroll-period";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions", "recipe-seeds.json"
    ];

    [TestCase("Kann ich die Periode der Gruppe Bern jetzt schliessen?", "de")]
    [TestCase("Kann ich den Monat August schon abschliessen?", "de")]
    [TestCase("Darf ich die Periode schon abschliessen?", "de")]
    [TestCase("Können wir die Periode jetzt schliessen?", "de")]
    [TestCase("Dürfen wir die Periode schon versiegeln?", "de")]
    [TestCase("Can I close the period for Bern now?", "en")]
    [TestCase("Can we close the month yet?", "en")]
    [TestCase("May I close the period?", "en")]
    [TestCase("Am I allowed to close the period already?", "en")]
    [TestCase("Puis-je clôturer le mois maintenant ?", "fr")]
    [TestCase("Est-ce que je peux clôturer le mois ?", "fr")]
    [TestCase("Peut-on clôturer le mois ?", "fr")]
    [TestCase("Posso chiudere il periodo adesso?", "it")]
    [TestCase("Possiamo chiudere il mese?", "it")]
    public void AdviceQuestion_DoesNotStartTheSealRecipe(string message, string language)
    {
        RecipeTriggerMatcher.Matches(CloseTrigger(), null, message, language).ShouldBeFalse();
    }

    [TestCase("Schliesse die Periode Mai ab", "de")]
    [TestCase("Kannst du die Periode abschliessen", "de")]
    [TestCase("Close the period for May", "en")]
    [TestCase("Can you close the period", "en")]
    [TestCase("Clôture le mois de mai", "fr")]
    [TestCase("Chiudi il periodo di maggio", "it")]
    public void CloseRequest_KeepsStartingTheSealRecipe(string message, string language)
    {
        RecipeTriggerMatcher.Matches(CloseTrigger(), null, message, language).ShouldBeTrue();
    }

    private static RecipeTrigger CloseTrigger()
    {
        var path = RepositoryRootLocator.RequireFile(DefinitionsRelativePath);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var recipe = document.RootElement.GetProperty("recipes")
            .EnumerateArray()
            .Single(element => element.GetProperty("name").GetString() == CloseRecipe);

        return recipe.GetProperty("trigger").Deserialize<RecipeTrigger>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}
