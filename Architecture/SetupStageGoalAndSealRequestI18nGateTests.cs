// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the texts of the stage-aware setup goal and the welcome seal bubble in all 25 UI languages: the four
/// core catalogues of Klacks.Ui and the 21 language-plugin catalogues. A key missing in one of them shows the
/// fallback language in exactly the place where Klacksy proposes an irreversible step (sealing orders), and a
/// goal rationale without its two placeholders shows raw braces. Inconclusive when Klacks.Ui is not checked out.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class SetupStageGoalAndSealRequestI18nGateTests
{
    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private const string UiCatalogueRelativePath = "Klacks.Ui/src/assets/i18n";
    private const string PluginLanguagesRelativePath = "Klacks.Api/Plugins/Languages";
    private const string TranslationsFileName = "translations.json";
    private const string JsonExtension = ".json";
    private const string CountPlaceholder = "{{count}}";
    private const string DaysPlaceholder = "{{days}}";
    private const int ExpectedCatalogues = 25;
    private const string ChatOnlyGoalHintKey = "assistant-chat.goal-candidates.chat-hint";
    private const string ChatBusyKey = "assistant-chat.goal-candidates.chat-busy";

    [Test]
    public void EveryNewKey_ExistsAndIsFilledInAllTwentyFiveCatalogues()
    {
        var catalogues = LoadCatalogues();
        if (catalogues == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiCatalogueRelativePath));
            return;
        }

        catalogues.Count.ShouldBe(ExpectedCatalogues);
        foreach (var (name, texts) in catalogues)
        {
            foreach (var key in RequiredKeys())
            {
                texts.TryGetValue(key, out var text).ShouldBeTrue($"'{key}' is missing from the '{name}' catalogue.");
                string.IsNullOrWhiteSpace(text).ShouldBeFalse($"'{key}' is empty in the '{name}' catalogue.");
            }
        }
    }

    [Test]
    public void EveryStageRationale_InterpolatesCountAndDaysInAllCatalogues()
    {
        var catalogues = LoadCatalogues();
        if (catalogues == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiCatalogueRelativePath));
            return;
        }

        foreach (var (name, texts) in catalogues)
        {
            foreach (var key in StageDefinitions().Select(definition => definition.RationaleKey))
            {
                texts[key].ShouldContain(CountPlaceholder, customMessage: $"'{key}' in '{name}'");
                texts[key].ShouldContain(DaysPlaceholder, customMessage: $"'{key}' in '{name}'");
            }
        }
    }

    private static IEnumerable<GoalTypeDefinitionKeys> StageDefinitions() =>
        new[] { ScheduleSetupStage.NothingYet, ScheduleSetupStage.OrdersButNoShifts }
            .Select(GoalTypeCatalog.FindForStage)
            .Select(definition => new GoalTypeDefinitionKeys(definition.TitleKey, definition.RationaleKey));

    private static IEnumerable<string> RequiredKeys() =>
        StageDefinitions()
            .SelectMany(definition => new[] { definition.TitleKey, definition.RationaleKey })
            .Concat(
            [
                WelcomeFocusI18nKeys.NoShiftsPrompt,
                WelcomeFocusI18nKeys.NoShiftsAction,
                WelcomeFocusI18nKeys.SetupConsultationAccept,
                "klacksy.orders.review.request",
                SetupChatPromptKeys.SetupConsultationTrigger,
                ChatOnlyGoalHintKey,
                ChatBusyKey,
            ]);

    private static List<(string Name, Dictionary<string, string> Texts)>? LoadCatalogues()
    {
        var uiDirectory = RepositoryRootLocator.FindDirectory(UiCatalogueRelativePath);
        var pluginDirectory = RepositoryRootLocator.FindDirectory(PluginLanguagesRelativePath);
        if (uiDirectory == null || pluginDirectory == null)
        {
            return null;
        }

        return CoreLanguages
            .Select(language => (Name: language, Path: Path.Combine(uiDirectory, language + JsonExtension)))
            .Concat(Directory.GetDirectories(pluginDirectory)
                .Select(directory => (Name: Path.GetFileName(directory), Path: Path.Combine(directory, TranslationsFileName)))
                .Where(catalogue => File.Exists(catalogue.Path)))
            .Select(catalogue => (catalogue.Name, Read(catalogue.Path)))
            .ToList();
    }

    private static Dictionary<string, string> Read(string path) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
        ?? new Dictionary<string, string>();

    private sealed record GoalTypeDefinitionKeys(string TitleKey, string RationaleKey);
}
