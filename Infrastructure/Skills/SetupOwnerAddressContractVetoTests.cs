// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the contract veto of the setup-owner-address recipe. That recipe configures the company-wide holiday
/// calendar; a message that switches the calendar of one CONTRACT must not start it. Live 2026-10-10: "Ändere den
/// Vertrag ... so, dass er den Feiertagskalender Zürich verwendet" engaged the recipe, which then forced
/// update_owner_locale_settings (the company calendar) instead of update_contract. The veto words (vertrag,
/// contract, contrat, contratto) sit in the sibling-intent noneOf block of the recipe; the recipe's own
/// company-calendar sentences must keep matching.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SetupOwnerAddressContractVetoTests
{
    private const string RecipeName = "setup-owner-address";
    private const string RecipeSeedsFileName = "recipe-seeds.json";
    private const string TriggerProperty = "trigger";

    [TestCase("Ändere den Vertrag 'Vollzeit 160 BE' bitte so, dass er den Feiertagskalender Zürich verwendet.", "de")]
    [TestCase("Stell den Feiertagskalender des Vertrags Teilzeit 80 auf Bern um.", "de")]
    [TestCase("Setze beim Arbeitsvertrag den Feiertagskalender auf Zürich.", "de")]
    [TestCase("Change the contract so that it uses the Zurich holiday calendar.", "en")]
    [TestCase("Update the holiday calendar of the contract Full time 160 to Bern.", "en")]
    [TestCase("Modifier le calendrier des jours fériés du contrat Temps plein 160.", "fr")]
    [TestCase("Modifica il calendario festivo del contratto Tempo pieno 160.", "it")]
    public void ChangingTheCalendarOfAContract_DoesNotStartTheCompanyCalendarRecipe(string message, string language)
    {
        RecipeTriggerMatcher.Matches(LoadTrigger(), null, message, language).ShouldBeFalse();
    }

    [TestCase("Ändere den Feiertagskalender auf Kanton Zürich.", "de")]
    [TestCase("Setze den Feiertagskalender unseres Unternehmens auf Bern.", "de")]
    [TestCase("Configure the company holiday calendar for Zurich.", "en")]
    [TestCase("Choose the calendar for Bern.", "en")]
    [TestCase("Configurer le calendrier des jours fériés de Genève.", "fr")]
    [TestCase("Imposta il calendario festivo del Ticino.", "it")]
    public void ChangingTheCompanyCalendar_StillStartsTheRecipe(string message, string language)
    {
        RecipeTriggerMatcher.Matches(LoadTrigger(), null, message, language).ShouldBeTrue();
    }

    private static RecipeTrigger LoadTrigger()
    {
        var path = RepositoryRootLocator.FindFile("Klacks.Api", "Application", "Skills", "Definitions", RecipeSeedsFileName);
        path.ShouldNotBeNull();

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var recipe = document.RootElement.GetProperty("recipes").EnumerateArray()
            .Single(element => element.GetProperty("name").GetString() == RecipeName);

        return JsonSerializer.Deserialize<RecipeTrigger>(
            recipe.GetProperty(TriggerProperty).GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}
