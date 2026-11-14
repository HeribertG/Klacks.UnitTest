// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The consent page catalogue: the four core languages carry every key with its placeholders, every one of the 21
/// installed packs ships every key, with the placeholders and the numbers the page relies on (30 days, 24 hours), and the
/// startup loader makes each pack resolve in its own language. Also pins how a tag is claimed by a language and which
/// languages are right to left.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.OAuth;

[TestFixture]
public class OAuthConsentTextsTests
{
    private const int ExpectedPluginPacks = 21;
    private const string DaysNumber = "30";
    private const string HoursNumber = "24";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [TearDown]
    public void ResetConfiguredTexts()
    {
        OAuthConsentTexts.Reset();
    }

    private static IEnumerable<string> PackDirectories() =>
        Directory.GetDirectories(Path.Combine(RepositoryRootLocator.ApiProject, "Plugins", "Languages"))
            .Where(dir => File.Exists(Path.Combine(dir, LanguagePluginConstants.ManifestFileName)))
            .Where(dir => !LanguagePluginConstants.CoreLanguages.Contains(Path.GetFileName(dir)));

    private static Dictionary<string, string> PackTexts(string directory) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(directory, LanguagePluginConstants.AssistantTextsFileName)), JsonOptions)!;

    public static IEnumerable<string> RequiredKeys() => OAuthConsentTexts.RequiredKeys;

    [Test]
    public void TheCoreCatalogue_HoldsExactlyTheRequiredKeys()
    {
        OAuthConsentTexts.Keys.OrderBy(k => k).ShouldBe(OAuthConsentTexts.RequiredKeys.OrderBy(k => k));
    }

    [TestCaseSource(nameof(RequiredKeys))]
    public void EveryRequiredKey_HasAFilledSentenceInEveryCoreLanguage(string key)
    {
        var variants = OAuthConsentTexts.VariantsOf(key);

        foreach (var language in OAuthConsentTexts.CoreLanguages)
        {
            variants.TryGetValue(language, out var text).ShouldBeTrue($"{key} lacks {language}");
            text.ShouldNotBeNullOrWhiteSpace($"{key} is blank in {language}");
        }
    }

    [Test]
    public void ThePlaceholdersAndTheNumbers_StandInEveryCoreLanguage()
    {
        foreach (var language in OAuthConsentTexts.CoreLanguages)
        {
            OAuthConsentTexts.VariantsOf(OAuthConsentTexts.LocalProgramWarning)[language].ShouldContain(OAuthConsentTexts.TargetPlaceholder, customMessage: language);
            OAuthConsentTexts.VariantsOf(OAuthConsentTexts.Lifetime)[language].ShouldContain(OAuthConsentTexts.DurationPlaceholder, customMessage: language);
            OAuthConsentTexts.VariantsOf(OAuthConsentTexts.DurationDays)[language].ShouldContain(DaysNumber, customMessage: language);
            OAuthConsentTexts.VariantsOf(OAuthConsentTexts.DurationHours)[language].ShouldContain(HoursNumber, customMessage: language);
        }
    }

    [Test]
    public void TheDurationsInTheCatalogue_MatchTheTokenLifetimesOfTheServer()
    {
        OAuthConstants.AccessTokenExpiresInDays.ToString().ShouldBe(DaysNumber);
        OAuthConstants.PlaygroundAccessTokenExpiresInHours.ToString().ShouldBe(HoursNumber);
    }

    [Test]
    public void TheNumberOfPacks_IsStillTwentyOne()
    {
        PackDirectories().Count().ShouldBe(ExpectedPluginPacks);
    }

    [Test]
    public void EveryPack_ShipsEveryKey_WithItsPlaceholdersAndNumbers()
    {
        var problems = new List<string>();

        foreach (var directory in PackDirectories())
        {
            var code = Path.GetFileName(directory);
            var texts = PackTexts(directory);

            foreach (var key in OAuthConsentTexts.RequiredKeys)
            {
                if (!texts.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
                {
                    problems.Add($"{code}: missing '{key}'");
                }
            }

            Expect(problems, code, texts, OAuthConsentTexts.LocalProgramWarning, OAuthConsentTexts.TargetPlaceholder);
            Expect(problems, code, texts, OAuthConsentTexts.Lifetime, OAuthConsentTexts.DurationPlaceholder);
            Expect(problems, code, texts, OAuthConsentTexts.DurationDays, DaysNumber);
            Expect(problems, code, texts, OAuthConsentTexts.DurationHours, HoursNumber);
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryPack_TranslatesTheEnglishTitleAndTheIntro()
    {
        var english = OAuthConsentTexts.EnglishOf(OAuthConsentTexts.Title);
        var englishIntro = OAuthConsentTexts.EnglishOf(OAuthConsentTexts.Intro);

        foreach (var directory in PackDirectories())
        {
            var texts = PackTexts(directory);
            texts[OAuthConsentTexts.Title].ShouldNotBe(english, Path.GetFileName(directory));
            texts[OAuthConsentTexts.Intro].ShouldNotBe(englishIntro, Path.GetFileName(directory));
        }
    }

    [Test]
    public void TheStartupLoader_ResolvesEveryPacksTitleInItsOwnLanguage()
    {
        var failures = new List<string>();
        var english = OAuthConsentTexts.EnglishOf(OAuthConsentTexts.Title);

        AssistantTextsPluginLoader.Load(RepositoryRootLocator.ApiProject, (file, ex) => failures.Add($"{file}: {ex.Message}"));

        foreach (var directory in PackDirectories())
        {
            var code = Path.GetFileName(directory);
            if (!OAuthConsentTexts.TryGetText(OAuthConsentTexts.Title, code, out var text))
            {
                failures.Add($"{code}: the loaded catalogue has no title");
            }
            else if (string.Equals(text, english, StringComparison.Ordinal))
            {
                failures.Add($"{code}: resolved to the English core text");
            }
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    [TestCase("de", "de")]
    [TestCase("de-CH", "de")]
    [TestCase("DE", "DE")]
    [TestCase("en-GB", "en")]
    [TestCase("fr-CA", "fr")]
    [TestCase("it", "it")]
    public void ResolveClaimedLanguage_CoreLanguageOrItsRegionalTag_IsClaimedByTheCoreLanguage(string tag, string expected)
    {
        OAuthConsentTexts.ResolveClaimedLanguage(tag).ShouldBe(expected);
    }

    [TestCase("pt-BR", "pt")]
    [TestCase("zh-TW", "zh-TW")]
    [TestCase("zh-CN", "zh-CN")]
    [TestCase("ar-EG", "ar")]
    public void ResolveClaimedLanguage_ConfiguredPack_IsClaimedByTheFullTagBeforeTheBaseLanguage(string tag, string expected)
    {
        AssistantTextsPluginLoader.Load(RepositoryRootLocator.ApiProject);

        OAuthConsentTexts.ResolveClaimedLanguage(tag).ShouldBe(expected);
    }

    [TestCase("pt-BR")]
    [TestCase("ar")]
    [TestCase("zh-HK")]
    [TestCase("xx")]
    [TestCase("")]
    [TestCase(null)]
    public void ResolveClaimedLanguage_PackNotLoaded_ClaimsNothingAndNeverFallsBackToEnglish(string? tag)
    {
        OAuthConsentTexts.ResolveClaimedLanguage(tag).ShouldBeNull();
    }

    [TestCase("ar", true)]
    [TestCase("he", true)]
    [TestCase("ar-EG", true)]
    [TestCase("he-IL", true)]
    [TestCase("HE", true)]
    [TestCase("de", false)]
    [TestCase("en", false)]
    [TestCase("zh-CN", false)]
    [TestCase("ja", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void IsRightToLeft_OnlyArabicAndHebrew(string? tag, bool expected)
    {
        LanguageTag.IsRightToLeft(tag).ShouldBe(expected);
    }

    private static void Expect(List<string> problems, string code, Dictionary<string, string> texts, string key, string fragment)
    {
        if (texts.TryGetValue(key, out var text) && !text.Contains(fragment, StringComparison.Ordinal))
        {
            problems.Add($"{code}: '{key}' has no {fragment}");
        }
    }
}
