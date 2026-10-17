// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the label of the "explain briefly first" chip (ExplainOfferTexts) in all 25 languages. The packs are loaded through
/// the real startup loader, so a pack whose file carries the key but is never fed into the catalogue fails here too. Every
/// language resolves the label in its own right (never through the English fallback), as a usable one-line chip text; it
/// differs from English in every other language (catches a copied instead of translated pack) and from the language's own
/// yes/no words (the chip is recognised by its label, so it must never collide with a confirmation), and the real affirmation and
/// refusal detectors, loaded with every shipped language pack, read none of the 25 labels as a yes or a no.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ExplainOfferTextCatalogueGuardTests
{
    private const string PluginsDirectory = "Plugins";
    private const string LanguagesDirectory = "Languages";
    private const int ExpectedPluginPacks = 21;
    private const int ExpectedLanguages = 25;
    private const int MaxLabelLength = 40;
    private const string GermanRegional = "de-CH";
    private const string German = "de";
    private const string ChineseSimplified = "zh-CN";
    private const string ChineseTraditional = "zh-TW";
    private const string UnknownLanguage = "xx-XX";
    private static readonly char[] ForbiddenCharacters = ['{', '}', '\n', '\r', '\t'];

    [SetUp]
    public void LoadThePacks()
    {
        AssistantTextCatalogues.ResetAll();
        var failures = new List<string>();
        AssistantTextsPluginLoader.Load(RepositoryRootLocator.ApiProject, (file, ex) => failures.Add($"{file}: {ex.Message}"));
        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    [TearDown]
    public void ResetConfiguredTexts() => AssistantTextCatalogues.ResetAll();

    private static IReadOnlyList<string> PackLanguages() =>
        Directory.GetDirectories(Path.Combine(RepositoryRootLocator.ApiProject, PluginsDirectory, LanguagesDirectory))
            .Where(dir => File.Exists(Path.Combine(dir, LanguagePluginConstants.ManifestFileName)))
            .Select(dir => Path.GetFileName(dir))
            .Where(code => !LanguagePluginConstants.CoreLanguages.Contains(code))
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<string> AllLanguages() => ExplainOfferTexts.CoreLanguages.Concat(PackLanguages()).ToList();

    private static string OwnLabel(string language)
    {
        ExplainOfferTexts.TryGetOwnText(ExplainOfferTexts.ChipLabel, language, out var text)
            .ShouldBeTrue($"{language}: no own chip label");
        return text;
    }

    [Test]
    public void TheCatalogue_CoversAllTwentyFiveLanguages()
    {
        PackLanguages().Count.ShouldBe(ExpectedPluginPacks);
        AllLanguages().Count.ShouldBe(ExpectedLanguages);
    }

    [Test]
    public void TheCoreTable_CarriesExactlyTheRequiredKeys()
    {
        ExplainOfferTexts.Keys.ShouldBe(ExplainOfferTexts.RequiredKeys, ignoreOrder: true);
    }

    [Test]
    public void EveryLanguage_ResolvesTheLabelInItsOwnRight_AsAUsableOneLineChip()
    {
        var problems = new List<string>();

        foreach (var language in AllLanguages())
        {
            if (!ExplainOfferTexts.TryGetOwnText(ExplainOfferTexts.ChipLabel, language, out var text)
                || string.IsNullOrWhiteSpace(text))
            {
                problems.Add($"{language}: no chip label");
                continue;
            }

            if (text != text.Trim() || text.IndexOfAny(ForbiddenCharacters) >= 0 || text.Length > MaxLabelLength)
            {
                problems.Add($"{language}: not a usable one-line chip label: '{text}'");
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void TheLabel_DiffersFromEnglish_InEveryOtherLanguage()
    {
        var english = ExplainOfferTexts.EnglishOf(ExplainOfferTexts.ChipLabel);
        var problems = AllLanguages()
            .Where(code => code != LanguageConfig.DefaultLanguageFallback)
            .Where(code => string.Equals(OwnLabel(code), english, StringComparison.Ordinal))
            .Select(code => $"{code}: the chip label is still the English text")
            .ToList();

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void TheLabel_NeverCollidesWithTheLanguagesOwnYesOrNo()
    {
        var problems = new List<string>();

        foreach (var language in AllLanguages())
        {
            var label = OwnLabel(language);
            foreach (var key in new[] { GracefulCorrectionTexts.RecipeConfirmYes, GracefulCorrectionTexts.RecipeConfirmNo })
            {
                if (GracefulCorrectionTexts.TryGetText(key, language, out var word)
                    && string.Equals(label, word, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{language}: the chip label equals '{word}'");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// The chip label is sent as the user's next message while a confirmation may be held. Read by the real detectors with the
    /// vocabularies of the core languages AND all shipped language packs, under its own UI language and under an unknown one, it
    /// must be neither a yes (AssessExplicitConfirmation would force confirm_pending_action and run the held action; an
    /// Ambiguous verdict would inject the "ask yes or no" note into the explanation turn), nor a plain affirmation (a paused
    /// recipe gate would proceed), nor a refusal (a paused recipe would be aborted as declined).
    /// </summary>
    [Test]
    public void EveryLabel_IsNeitherAYesNorANo_ForTheRealDetectors()
    {
        ShippedConversationSignals.Install();
        try
        {
            var problems = new List<string>();
            var labels = AllLanguages().Select(language => (Language: language, Label: OwnLabel(language))).ToList();

            foreach (var (language, label) in labels)
            {
                foreach (var readAs in new[] { language, null })
                {
                    var explicitVerdict = AffirmationDetector.AssessExplicitConfirmation(label, readAs).Verdict;
                    var affirmationVerdict = AffirmationDetector.AssessAffirmation(label, readAs).Verdict;
                    if (explicitVerdict != ConfirmationVerdict.NotConfirmed)
                    {
                        problems.Add($"{language} '{label}' read as {readAs ?? "unknown"}: explicit confirmation {explicitVerdict}");
                    }

                    if (affirmationVerdict != ConfirmationVerdict.NotConfirmed)
                    {
                        problems.Add($"{language} '{label}' read as {readAs ?? "unknown"}: affirmation {affirmationVerdict}");
                    }

                    if (DeclineDetector.LeadsWithNegation(label, readAs) || DeclineDetector.IsBareNegation(label, readAs))
                    {
                        problems.Add($"{language} '{label}' read as {readAs ?? "unknown"}: refusal");
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

    [Test]
    public void EveryLabel_IsRecognisedAsAcceptance_WhateverTheLanguage()
    {
        var all = ExplainOfferTexts.AllTextsOf(ExplainOfferTexts.ChipLabel);

        foreach (var language in AllLanguages())
        {
            all.ShouldContain(OwnLabel(language), $"{language}: the label is not among AllTextsOf");
        }
    }

    [Test]
    public void RegionalTags_ReachTheirLanguage_AndTheTwoChinesePacksStaySeparate()
    {
        OwnLabel(GermanRegional).ShouldBe(OwnLabel(German));
        OwnLabel(ChineseSimplified).ShouldNotBe(OwnLabel(ChineseTraditional));
    }

    [Test]
    public void AnUnknownLanguage_ClaimsNothing_ButStillResolvesToEnglish()
    {
        ExplainOfferTexts.TryGetOwnText(ExplainOfferTexts.ChipLabel, UnknownLanguage, out _).ShouldBeFalse();
        ExplainOfferTexts.TryGetText(ExplainOfferTexts.ChipLabel, UnknownLanguage, out var text).ShouldBeTrue();
        text.ShouldBe(ExplainOfferTexts.EnglishOf(ExplainOfferTexts.ChipLabel));
    }
}
