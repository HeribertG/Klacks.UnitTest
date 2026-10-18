// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the trigger phrase of the FT/+FT explain offer (T5, shift.holidayOptions.explainOffer.triggerPhrase). The
/// "yes" chip sends it as the user's own chat message, and the chat request has no skill parameter: happen 77
/// (explain_shift_holiday_options) reaches the toolset only through the keyword guarantee. So for each of the 25
/// languages the phrase is run through the real SkillMatchingEngine against the whole shipped seed catalogue - with that
/// language pack's skill-synonyms.json merged in, as the plugin installer does - and 77 must be inside the guaranteed
/// cap. The phrase must also be neither a yes nor a no for the real detectors, so it cannot redeem or refuse a held
/// confirmation of the same conversation. Not covered: the later toolset truncation and the model's choice between
/// 77 and the route or concept explain skills - only a goldset run measures that. Inconclusive when Klacks.Ui is not
/// checked out next to Klacks.Api.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class UiExplainOfferTriggerPhraseGuardTests
{
    private const string TriggerPhraseKey = "shift.holidayOptions.explainOffer.triggerPhrase";
    private const string UiCatalogueRelativePath = "Klacks.Ui/src/assets/i18n";
    private const string PluginLanguagesRelativePath = "Klacks.Api/Plugins/Languages";
    private const string TranslationsFileName = "translations.json";
    private const string SkillSynonymsFileName = "skill-synonyms.json";
    private const string JsonExtension = ".json";
    private const int ExpectedLanguages = 25;

    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    [Test]
    public void TheTriggerPhrase_GuaranteesTheHolidayOptionsHappen_InEveryLanguage()
    {
        var phrases = PhrasesOrInconclusive();
        var problems = new List<string>();

        foreach (var (language, phrase, packSynonyms) in phrases)
        {
            var catalogue = SeedSkillCatalog.Load();
            if (packSynonyms != null)
            {
                MergePackSynonyms(catalogue, language, packSynonyms);
            }

            var guaranteed = SkillMatchingEngine.TopKeywordMatchedSkillNames(catalogue, phrase);
            if (!guaranteed.Contains(SkillNames.ExplainShiftHolidayOptions))
            {
                problems.Add($"{language}: '{phrase}' guarantees [{string.Join(", ", guaranteed)}]");
            }
        }

        phrases.Count.ShouldBe(ExpectedLanguages);
        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void TheTriggerPhrase_IsNeitherAYesNorANo_ForTheRealDetectors()
    {
        var phrases = PhrasesOrInconclusive();
        ShippedConversationSignals.Install();
        try
        {
            var problems = new List<string>();
            foreach (var (language, phrase, _) in phrases)
            {
                foreach (var readAs in new[] { language, null })
                {
                    if (AffirmationDetector.AssessExplicitConfirmation(phrase, readAs).Verdict != ConfirmationVerdict.NotConfirmed
                        || AffirmationDetector.AssessAffirmation(phrase, readAs).Verdict != ConfirmationVerdict.NotConfirmed)
                    {
                        problems.Add($"{language} '{phrase}' read as {readAs ?? "unknown"}: confirmation");
                    }

                    if (DeclineDetector.LeadsWithNegation(phrase, readAs) || DeclineDetector.IsBareNegation(phrase, readAs))
                    {
                        problems.Add($"{language} '{phrase}' read as {readAs ?? "unknown"}: refusal");
                    }
                }
            }

            problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
        }
        finally
        {
            ShippedConversationSignals.Uninstall();
        }
    }

    private static List<(string Language, string Phrase, Dictionary<string, List<string>>? PackSynonyms)> PhrasesOrInconclusive()
    {
        var catalogueDirectory = RepositoryRootLocator.FindDirectory(UiCatalogueRelativePath);
        var pluginDirectory = RepositoryRootLocator.FindDirectory(PluginLanguagesRelativePath);
        if (catalogueDirectory == null || pluginDirectory == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiCatalogueRelativePath));
        }

        var phrases = new List<(string, string, Dictionary<string, List<string>>?)>();
        foreach (var language in CoreLanguages)
        {
            phrases.Add((language, ReadPhrase(Path.Combine(catalogueDirectory!, language + JsonExtension)), null));
        }

        foreach (var directory in Directory.GetDirectories(pluginDirectory!))
        {
            var translations = Path.Combine(directory, TranslationsFileName);
            var synonyms = Path.Combine(directory, SkillSynonymsFileName);
            if (!File.Exists(translations) || !File.Exists(synonyms))
            {
                continue;
            }

            phrases.Add((
                Path.GetFileName(directory),
                ReadPhrase(translations),
                JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(synonyms))));
        }

        return phrases;
    }

    private static string ReadPhrase(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty(TriggerPhraseKey, out var value) ? value.GetString() ?? string.Empty : string.Empty;
    }

    private static void MergePackSynonyms(
        List<AgentSkill> catalogue, string language, Dictionary<string, List<string>> packSynonyms)
    {
        var code = language.ToLowerInvariant();
        foreach (var skill in catalogue)
        {
            if (!packSynonyms.TryGetValue(skill.Name, out var synonyms) || synonyms.Count == 0)
            {
                continue;
            }

            skill.Synonyms ??= new Dictionary<string, List<string>>();
            skill.Synonyms[code] = synonyms;
        }
    }
}
