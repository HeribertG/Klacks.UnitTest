// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the two server-written text catalogues that used to speak only de/en/fr/it in all 25 languages:
/// the escalation handoff texts (EscalationHandoffTexts: four core languages in code, 21 packs in
/// assistant-texts.json) and the messenger proactive texts (MessengerProactiveTexts: four core languages in
/// code, 21 packs read from the assistant.proactive.* keys of translations.json), both joined at startup by
/// AssistantTextsPluginLoader. Every language has every key and no text is empty; the escalation texts of no
/// non-English language equal the English wording (a pack that was never translated); the placeholders of
/// every text are exactly those of the English text AND exactly those the code fills (the notifier's
/// parameters, and the SummaryParams of the real trigger events); no text keeps a stray brace; emoji, bold
/// markers and line breaks match the English text; and every pack's assistant-texts.json carries all 29
/// required keys of the three catalogues that read it. The behaviour tests pin the resolution rules: an
/// unknown language is English, a regional tag reaches its language, the zh-CN and zh-TW casing resolves, a
/// pack language resolves to its own text and never to English, and a loaded pack that lacks a key resolves
/// to nothing instead of silently to English.
/// </summary>

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Application.Services.Assistant.Escalation;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Services.Common;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class EscalationAndProactiveTextCatalogueGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string PluginsDirectory = "Plugins";
    private const string LanguagesDirectory = "Languages";
    private const int ExpectedPluginPacks = 21;
    private const int ExpectedEscalationKeys = 5;
    private const int ExpectedProactiveKeys = 5;
    private const int ExpectedAssistantTextsKeys = 29;
    private const string Bold = "**";
    private const char LineBreak = '\n';
    private const int VariationSelector16 = 0xFE0F;
    private const int InformationSourceCodePoint = 0x2139;
    private const string StrayBracePattern = @"[{}]";
    private const string UnknownLanguage = "xx-XX";
    private const string GermanRegional = "de-CH";
    private const string JapaneseRegional = "ja-JP";
    private const string Japanese = "ja";
    private const string ChineseSimplified = "zh-CN";
    private const string ChineseTraditional = "zh-TW";
    private const string PortugueseRegional = "pt-BR";
    private const string Portuguese = "pt";
    private const string AbsenceName = "Erika";
    private const string SampleValue = "1";
    private const string ProactivePrefix = "assistant.proactive.";
    private const string TranslationsFileName = "translations.json";
    private const string ResponderParameter = EscalationHandoffPlaceholders.Responder;

    private static readonly IReadOnlyDictionary<string, string[]> ExpectedEscalationPlaceholders =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [EscalationHandoffTexts.AcknowledgedConfirmation] = [EscalationHandoffPlaceholders.Date, EscalationHandoffPlaceholders.Employee],
            [EscalationHandoffTexts.HandoffQuietNote] = [ResponderParameter, EscalationHandoffPlaceholders.Date, EscalationHandoffPlaceholders.Employee],
            [EscalationHandoffTexts.ApprovalAcknowledgedConfirmation] = [EscalationHandoffPlaceholders.Action, EscalationHandoffPlaceholders.Finding],
            [EscalationHandoffTexts.ApprovalHandoffQuietNote] = [ResponderParameter, EscalationHandoffPlaceholders.Action, EscalationHandoffPlaceholders.Finding],
            [EscalationHandoffTexts.ApprovalExhaustedNote] = [EscalationHandoffPlaceholders.Action, EscalationHandoffPlaceholders.Finding]
        };

    private static readonly IReadOnlyDictionary<(string Language, string Key), string> SameAsEnglishByDesign =
        new Dictionary<(string Language, string Key), string>();

    [SetUp]
    public void LoadThePacks()
    {
        ResetAll();
        var failures = new List<string>();
        AssistantTextsPluginLoader.Load(ApiRoot(), (file, ex) => failures.Add($"{file}: {ex.Message}"));
        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    [TearDown]
    public void ResetConfiguredTexts() => ResetAll();

    private static void ResetAll()
    {
        EscalationHandoffTexts.Reset();
        MessengerProactiveTexts.Reset();
        ClarificationTexts.Reset();
        GracefulCorrectionTexts.Reset();
    }

    private static string ApiRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory);
            if (Directory.Exists(Path.Combine(candidate, PluginsDirectory, LanguagesDirectory)))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate {ApiProjectDirectory}/{PluginsDirectory}/{LanguagesDirectory} by walking up from the test base directory.");
    }

    private static bool IsEnglish(string language) =>
        string.Equals(language, LanguageConfig.DefaultLanguageFallback, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> PackLanguages() =>
        Directory.GetDirectories(Path.Combine(ApiRoot(), PluginsDirectory, LanguagesDirectory))
            .Where(dir => File.Exists(Path.Combine(dir, LanguagePluginConstants.ManifestFileName)))
            .Select(dir => Path.GetFileName(dir))
            .Where(code => !LanguagePluginConstants.CoreLanguages.Contains(code))
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<string> AllLanguages() => MultiLanguage.CoreLanguages.Concat(PackLanguages()).ToList();

    private static Dictionary<string, string> PackFile(string language, string fileName)
    {
        var file = Path.Combine(ApiRoot(), PluginsDirectory, LanguagesDirectory, language, fileName);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))!;
    }

    private static string EscalationText(string key, string language)
    {
        EscalationHandoffTexts.TryGetText(key, language, out var text).ShouldBeTrue($"{language}: no text for '{key}'");
        return text;
    }

    private static string ProactiveText(string key, string language)
    {
        MessengerProactiveTexts.TryGetText(key, language, out var text).ShouldBeTrue($"{language}: no text for '{key}'");
        return text;
    }

    private static IReadOnlyList<(string Catalogue, string Key, Func<string, string> TextOf, string English)> AllTexts()
    {
        var texts = new List<(string, string, Func<string, string>, string)>();
        foreach (var key in EscalationHandoffTexts.RequiredKeys)
        {
            texts.Add(("escalation", key, language => EscalationText(key, language), EscalationHandoffTexts.EnglishOf(key)));
        }

        foreach (var key in MessengerProactiveTexts.CoveredKeys)
        {
            texts.Add(("proactive", key, language => ProactiveText(key, language), MessengerProactiveTexts.EnglishOf(key)));
        }

        return texts;
    }

    private static List<int> EmojiSequence(string text)
    {
        var emoji = new List<int>();
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value != VariationSelector16
                && (Rune.GetUnicodeCategory(rune) == UnicodeCategory.OtherSymbol || rune.Value == InformationSourceCodePoint))
            {
                emoji.Add(rune.Value);
            }
        }

        return emoji;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ProactiveEventParameters()
    {
        var group = new[] { Guid.NewGuid() };
        var day = new DateOnly(2026, 9, 25);
        var due = new DateTime(2026, 9, 25, 6, 0, 0, DateTimeKind.Utc);

        IAgentTriggerEvent[] events =
        [
            new UnstaffedShiftTriggerEvent(Guid.NewGuid(), day, 1, group),
            new WorkDroppedByErpImportTriggerEvent(Guid.NewGuid(), AbsenceName, day, group),
            new OrderImportFailedTriggerEvent(Guid.NewGuid(), "orders.csv", "boom"),
            new EscalationStageAlertTriggerEvent(Guid.NewGuid(), Guid.NewGuid().ToString(), AbsenceName, due, due, TimeZoneInfo.Utc),
            new AgentConditionDigestTriggerEvent(Guid.NewGuid(), day, 4, 1, 1, 2, 1, [])
        ];

        return events.ToDictionary(
            triggerEvent => triggerEvent.Summary[ProactiveMessageMarkers.I18nPrefix.Length..],
            triggerEvent => triggerEvent.SummaryParams!,
            StringComparer.Ordinal);
    }

    [Test]
    public void TheCatalogues_CoverAllTwentyFiveLanguages()
    {
        PackLanguages().Count.ShouldBe(ExpectedPluginPacks);
        AllLanguages().Count.ShouldBe(EscalationHandoffTexts.CoreLanguages.Count + ExpectedPluginPacks);
        EscalationHandoffTexts.CoreLanguages.ShouldBe(MultiLanguage.CoreLanguages, ignoreOrder: true);
        MessengerProactiveTexts.CoreLanguages.ShouldBe(MultiLanguage.CoreLanguages, ignoreOrder: true);
    }

    [Test]
    public void TheRequiredKeys_AreExactlyTheKeysTheCoreCataloguesCarry()
    {
        EscalationHandoffTexts.RequiredKeys.Count.ShouldBe(ExpectedEscalationKeys);
        EscalationHandoffTexts.RequiredKeys.Distinct(StringComparer.Ordinal).Count().ShouldBe(ExpectedEscalationKeys);
        EscalationHandoffTexts.Keys.ShouldBe(EscalationHandoffTexts.RequiredKeys, ignoreOrder: true);
        MessengerProactiveTexts.CoveredKeys.Count().ShouldBe(ExpectedProactiveKeys);
        MessengerProactiveTexts.CoveredKeys.ShouldAllBe(key => key.StartsWith(ProactivePrefix, StringComparison.Ordinal));
    }

    [Test]
    public void EveryPacksAssistantTexts_CarriesAllRequiredKeysOfTheThreeCataloguesThatReadIt()
    {
        var required = GracefulCorrectionTexts.RequiredKeys
            .Concat(ClarificationTextKeys.RequiredKeys)
            .Concat(EscalationHandoffTexts.RequiredKeys)
            .ToList();
        required.Distinct(StringComparer.Ordinal).Count().ShouldBe(ExpectedAssistantTextsKeys);
        var problems = new List<string>();

        foreach (var language in PackLanguages())
        {
            var texts = PackFile(language, LanguagePluginConstants.AssistantTextsFileName);
            problems.AddRange(required
                .Where(key => !texts.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
                .Select(key => $"{language}: assistant-texts.json is missing or has an empty '{key}'"));
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryPacksTranslations_CarriesAllProactiveKeysTheMessengerReads()
    {
        var problems = new List<string>();

        foreach (var language in PackLanguages())
        {
            var translations = PackFile(language, TranslationsFileName);
            problems.AddRange(MessengerProactiveTexts.CoveredKeys
                .Where(key => !translations.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
                .Select(key => $"{language}: {TranslationsFileName} is missing or has an empty '{key}'"));
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryLanguage_HasEveryKeyOfBothCatalogues_AndNoTextIsEmpty()
    {
        var problems = new List<string>();

        foreach (var language in AllLanguages())
        {
            foreach (var key in EscalationHandoffTexts.RequiredKeys)
            {
                if (!EscalationHandoffTexts.TryGetText(key, language, out var text) || string.IsNullOrWhiteSpace(text))
                {
                    problems.Add($"{language}: missing or empty escalation '{key}'");
                }
            }

            foreach (var key in MessengerProactiveTexts.CoveredKeys)
            {
                if (!MessengerProactiveTexts.TryGetText(key, language, out var text) || string.IsNullOrWhiteSpace(text))
                {
                    problems.Add($"{language}: missing or empty proactive '{key}'");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryNonEnglishLanguage_ResolvesEveryKeyInItsOwnLanguage_NotInEnglish()
    {
        var problems = new List<string>();

        foreach (var language in AllLanguages().Where(code => !IsEnglish(code)))
        {
            foreach (var (catalogue, key, textOf, english) in AllTexts())
            {
                if (string.Equals(textOf(language), english, StringComparison.Ordinal)
                    && !SameAsEnglishByDesign.ContainsKey((language, key)))
                {
                    problems.Add($"{language}: {catalogue} '{key}' equals the English text");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryEnglishIdenticalException_StillEqualsTheEnglishText_SoTheListCannotRot()
    {
        var problems = new List<string>();

        foreach (var ((language, key), reason) in SameAsEnglishByDesign)
        {
            var entry = AllTexts().Single(text => text.Key == key);
            if (!string.Equals(entry.TextOf(language), entry.English, StringComparison.Ordinal))
            {
                problems.Add($"{language}: '{key}' no longer equals the English text - remove the exception ({reason})");
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void TheEnglishEscalationTexts_CarryExactlyThePlaceholdersTheNotifierFills()
    {
        foreach (var key in EscalationHandoffTexts.RequiredKeys)
        {
            DoubleBraceTemplate.PlaceholdersOf(EscalationHandoffTexts.EnglishOf(key))
                .ShouldBe(ExpectedEscalationPlaceholders[key], ignoreOrder: true, customMessage: key);
        }
    }

    [Test]
    public void TheEnglishProactiveTexts_CarryExactlyTheParametersTheTriggerEventsEmit()
    {
        var emitted = ProactiveEventParameters();

        emitted.Keys.ShouldBe(MessengerProactiveTexts.CoveredKeys, ignoreOrder: true);
        foreach (var key in MessengerProactiveTexts.CoveredKeys)
        {
            DoubleBraceTemplate.PlaceholdersOf(MessengerProactiveTexts.EnglishOf(key))
                .ShouldBe(emitted[key].Keys, ignoreOrder: true, customMessage: key);
        }
    }

    [Test]
    public void EveryLanguage_CarriesExactlyThePlaceholdersOfTheEnglishText()
    {
        var problems = new List<string>();

        foreach (var language in AllLanguages())
        {
            foreach (var (catalogue, key, textOf, english) in AllTexts())
            {
                var expected = DoubleBraceTemplate.PlaceholdersOf(english);
                var actual = DoubleBraceTemplate.PlaceholdersOf(textOf(language));
                if (!expected.SetEquals(actual))
                {
                    problems.Add($"{language}: {catalogue} '{key}' has {{{string.Join(",", actual)}}}, expected {{{string.Join(",", expected)}}}");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryProactiveText_RendersWithTheParametersOfItsEvent_WithoutAPlaceholderLeftStanding()
    {
        var emitted = ProactiveEventParameters();
        var problems = new List<string>();

        foreach (var language in AllLanguages())
        {
            foreach (var key in MessengerProactiveTexts.CoveredKeys)
            {
                var rendered = DoubleBraceTemplate.Render(ProactiveText(key, language), emitted[key]);
                if (rendered.Contains("{{", StringComparison.Ordinal) || rendered.Contains("}}", StringComparison.Ordinal))
                {
                    problems.Add($"{language}: '{key}' still shows a placeholder after rendering: {rendered}");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void NoText_KeepsAStrayBrace()
    {
        var problems = new List<string>();

        foreach (var language in AllLanguages())
        {
            foreach (var (catalogue, key, textOf, _) in AllTexts())
            {
                var withoutPlaceholders = DoubleBraceTemplate.Render(
                    textOf(language),
                    DoubleBraceTemplate.PlaceholdersOf(textOf(language)).ToDictionary(name => name, _ => string.Empty));
                if (Regex.IsMatch(withoutPlaceholders, StrayBracePattern))
                {
                    problems.Add($"{language}: {catalogue} '{key}' keeps a stray brace");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryLanguage_KeepsEmojiBoldMarkersAndLineBreaksOfTheEnglishText()
    {
        var problems = new List<string>();

        foreach (var language in AllLanguages())
        {
            foreach (var (catalogue, key, textOf, english) in AllTexts())
            {
                var text = textOf(language);

                if (!EmojiSequence(text).SequenceEqual(EmojiSequence(english)))
                {
                    problems.Add($"{language}: {catalogue} '{key}' has a different emoji sequence");
                }

                if (Regex.Count(text, Regex.Escape(Bold)) != Regex.Count(english, Regex.Escape(Bold)))
                {
                    problems.Add($"{language}: {catalogue} '{key}' has a different number of {Bold}");
                }

                if (text.Count(c => c == LineBreak) != english.Count(c => c == LineBreak))
                {
                    problems.Add($"{language}: {catalogue} '{key}' has a different number of line breaks");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void AnUnknownLanguage_FallsBackToEnglish_ForEveryKey()
    {
        foreach (var (_, key, textOf, english) in AllTexts())
        {
            textOf(UnknownLanguage).ShouldBe(english, key);
        }

        EscalationHandoffTexts.TryGetText(EscalationHandoffTexts.HandoffQuietNote, null, out var withoutLanguage).ShouldBeTrue();
        withoutLanguage.ShouldBe(EscalationHandoffTexts.EnglishOf(EscalationHandoffTexts.HandoffQuietNote));
    }

    [Test]
    public void ARegionalTag_ReachesItsLanguage()
    {
        EscalationText(EscalationHandoffTexts.HandoffQuietNote, GermanRegional)
            .ShouldBe(EscalationHandoffTexts.VariantsOf(EscalationHandoffTexts.HandoffQuietNote)["de"]);
        EscalationText(EscalationHandoffTexts.HandoffQuietNote, PortugueseRegional)
            .ShouldBe(EscalationText(EscalationHandoffTexts.HandoffQuietNote, Portuguese));
        EscalationText(EscalationHandoffTexts.HandoffQuietNote, JapaneseRegional)
            .ShouldBe(EscalationText(EscalationHandoffTexts.HandoffQuietNote, Japanese));
        ProactiveText(ProactiveMessageI18nKeys.UnstaffedShift, JapaneseRegional)
            .ShouldBe(ProactiveText(ProactiveMessageI18nKeys.UnstaffedShift, Japanese));
        ProactiveText(ProactiveMessageI18nKeys.UnstaffedShift, Japanese)
            .ShouldNotBe(MessengerProactiveTexts.EnglishOf(ProactiveMessageI18nKeys.UnstaffedShift));
    }

    [TestCase("zh-cn", ChineseSimplified)]
    [TestCase("ZH-CN", ChineseSimplified)]
    [TestCase("zh-tw", ChineseTraditional)]
    [TestCase(ChineseTraditional, ChineseTraditional)]
    public void TheChinesePacks_ResolveInAnyCasing_AndNeverToEnglish(string tag, string pack)
    {
        EscalationText(EscalationHandoffTexts.AcknowledgedConfirmation, tag)
            .ShouldBe(EscalationText(EscalationHandoffTexts.AcknowledgedConfirmation, pack));
        EscalationText(EscalationHandoffTexts.AcknowledgedConfirmation, tag)
            .ShouldNotBe(EscalationHandoffTexts.EnglishOf(EscalationHandoffTexts.AcknowledgedConfirmation));
        ProactiveText(ProactiveMessageI18nKeys.DailyDigest, tag)
            .ShouldBe(ProactiveText(ProactiveMessageI18nKeys.DailyDigest, pack));
        ProactiveText(ProactiveMessageI18nKeys.DailyDigest, tag)
            .ShouldNotBe(MessengerProactiveTexts.EnglishOf(ProactiveMessageI18nKeys.DailyDigest));
    }

    [Test]
    public void TheTwoChinesePacks_DifferFromEachOther()
    {
        EscalationText(EscalationHandoffTexts.AcknowledgedConfirmation, ChineseSimplified)
            .ShouldNotBe(EscalationText(EscalationHandoffTexts.AcknowledgedConfirmation, ChineseTraditional));
        ProactiveText(ProactiveMessageI18nKeys.DailyDigest, ChineseSimplified)
            .ShouldNotBe(ProactiveText(ProactiveMessageI18nKeys.DailyDigest, ChineseTraditional));
    }

    [Test]
    public void AKnownLanguageWithAMissingKey_ResolvesToNothing_NotSilentlyToEnglish()
    {
        EscalationHandoffTexts.Configure(ChineseSimplified, new Dictionary<string, string> { ["some.other.key"] = "x" });
        MessengerProactiveTexts.Configure(ChineseSimplified, new Dictionary<string, string> { ["some.other.key"] = "x" });

        EscalationHandoffTexts.TryGetText(EscalationHandoffTexts.HandoffQuietNote, ChineseSimplified, out var escalation).ShouldBeFalse();
        escalation.ShouldBeEmpty();
        MessengerProactiveTexts.TryGetText(ProactiveMessageI18nKeys.DailyDigest, ChineseSimplified, out var proactive).ShouldBeFalse();
        proactive.ShouldBeEmpty();
    }

    [Test]
    public void ABlankPackText_CountsAsMissing()
    {
        EscalationHandoffTexts.Configure(
            ChineseSimplified, new Dictionary<string, string> { [EscalationHandoffTexts.HandoffQuietNote] = "   " });
        MessengerProactiveTexts.Configure(
            ChineseSimplified, new Dictionary<string, string> { [ProactiveMessageI18nKeys.DailyDigest] = "   " });

        EscalationHandoffTexts.TryGetText(EscalationHandoffTexts.HandoffQuietNote, ChineseSimplified, out _).ShouldBeFalse();
        MessengerProactiveTexts.TryGetText(ProactiveMessageI18nKeys.DailyDigest, ChineseSimplified, out _).ShouldBeFalse();
    }

    [Test]
    public void AKeyTheMessengerDoesNotCarry_IsReportedAsMissing_NotGuessed()
    {
        MessengerProactiveTexts.Covers(ProactiveMessageI18nKeys.MuteSuggestion).ShouldBeFalse();
        MessengerProactiveTexts.TryGetText(ProactiveMessageI18nKeys.MuteSuggestion, Japanese, out _).ShouldBeFalse();
    }

    [Test]
    public void TheGuardsOwnSampleValues_FillTheEnglishEscalationTextsCompletely()
    {
        var parameters = new Dictionary<string, string>
        {
            [EscalationHandoffPlaceholders.Date] = SampleValue,
            [EscalationHandoffPlaceholders.Employee] = SampleValue,
            [ResponderParameter] = SampleValue,
            [EscalationHandoffPlaceholders.Action] = SampleValue,
            [EscalationHandoffPlaceholders.Finding] = SampleValue
        };

        foreach (var key in EscalationHandoffTexts.RequiredKeys)
        {
            DoubleBraceTemplate.Render(EscalationHandoffTexts.EnglishOf(key), parameters).ShouldNotContain("{{", customMessage: key);
        }
    }
}
