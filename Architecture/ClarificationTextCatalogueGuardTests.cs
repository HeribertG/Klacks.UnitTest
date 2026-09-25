// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the localized texts of the inbound clarification dialog in all 25 languages (the four core
/// languages in code, the 21 packs in assistant-texts.json, joined by AssistantTextsPluginLoader): every
/// language has every key and no text is empty, no non-English language carries the English wording for any
/// of the 19 keys (a pack that was never translated; a legitimate coincidence needs a reasoned entry in
/// SameAsEnglishByDesign), the suggested-question text of every pack names the autonomy level and the kill
/// switch exactly as that pack's translations.json (the UI) calls them, the
/// placeholders of every translation are exactly those of the English text and of the code that fills them,
/// no text keeps a stray brace (the retired {level} and {killSwitch} included), emoji, bold markers and line
/// breaks match the English text, and the neutral reply subject is a valid single-line "Re:" subject. The
/// behaviour tests pin the resolution rules: an unknown language is English, a regional tag reaches its
/// language, the zh-CN and zh-TW casing resolves, and an installed language that lacks a key resolves to
/// nothing instead of silently to English (the loader-side gap, a pack directory without
/// assistant-texts.json, is treated as an unknown language and speaks English, and is only warned about).
/// </summary>

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Inbound;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ClarificationTextCatalogueGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string PluginsDirectory = "Plugins";
    private const string LanguagesDirectory = "Languages";
    private const int ExpectedPluginPacks = 21;
    private const int ExpectedRequiredKeys = 19;
    private const string Bold = "**";
    private const char LineBreak = '\n';
    private const int VariationSelector16 = 0xFE0F;
    private const int InformationSourceCodePoint = 0x2139;
    private const string StrayBracePattern = @"[{}]";
    private const string RetiredLevelPlaceholder = "{level}";
    private const string RetiredKillSwitchPlaceholder = "{killSwitch}";
    private const string UnknownLanguage = "xx-XX";
    private const string GermanRegional = "de-CH";
    private const string ChineseSimplified = "zh-CN";
    private const string ChineseTraditional = "zh-TW";
    private const string PortugueseRegional = "pt-BR";
    private const string Portuguese = "pt";
    private const string SkillSentenceMarker = "skillSentence";
    private const string TranslationsFileName = "translations.json";
    private const string AutonomyAssistedUiKey = "setting.autonomy.level-1";
    private const string KillSwitchUiKey = "setting.proactiveGovernance.kill-switch";

    private static readonly IReadOnlyDictionary<string, string[]> ExpectedPlaceholders =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [ClarificationTextKeys.PlannerStarted] = [ClarificationTextPlaceholders.Sender, ClarificationTextPlaceholders.Summary, ClarificationTextPlaceholders.Question, ClarificationTextPlaceholders.ShiftContext, ClarificationTextPlaceholders.Deadline],
            [ClarificationTextKeys.PlannerAnswerContext] = [ClarificationTextPlaceholders.Question, ClarificationTextPlaceholders.Asked, ClarificationTextPlaceholders.OriginalText],
            [ClarificationTextKeys.PlannerExpired] = [ClarificationTextPlaceholders.Sender, ClarificationTextPlaceholders.Question, ClarificationTextPlaceholders.Asked, ClarificationTextPlaceholders.Deadline, ClarificationTextPlaceholders.OriginalText, ClarificationTextPlaceholders.ShiftContext],
            [ClarificationTextKeys.PlannerSendFailed] = [ClarificationTextPlaceholders.Question],
            [ClarificationTextKeys.PlannerSuggested] = [ClarificationTextPlaceholders.Question],
            [ClarificationTextKeys.PlannerAnsweredAfterExpiry] = [ClarificationTextPlaceholders.Question, ClarificationTextPlaceholders.Asked],
            [ClarificationTextKeys.PlannerArrivedAfterClosure] = [ClarificationTextPlaceholders.Question, ClarificationTextPlaceholders.Asked, ClarificationTextPlaceholders.Status]
        };

    private static readonly IReadOnlyDictionary<(string Language, string Key), string> SameAsEnglishByDesign =
        new Dictionary<(string Language, string Key), string>();

    [SetUp]
    public void LoadThePacks()
    {
        var failures = new List<string>();
        AssistantTextsPluginLoader.Load(ApiRoot(), (file, ex) => failures.Add($"{file}: {ex.Message}"));
        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    [TearDown]
    public void ResetConfiguredTexts()
    {
        ClarificationTexts.Reset();
        GracefulCorrectionTexts.Reset();
        EscalationHandoffTexts.Reset();
        MessengerProactiveTexts.Reset();
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

    private static IReadOnlyList<string> AllLanguages() => ClarificationTexts.CoreLanguages.Concat(PackLanguages()).ToList();

    private static string TextOf(string key, string language)
    {
        ClarificationTexts.TryGetText(key, language, out var text).ShouldBeTrue($"{language}: no text for '{key}'");
        return text;
    }

    private static string English(string key) => ClarificationTexts.English(key);

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

    private static bool IsForbiddenInHeader(char character) =>
        char.IsControl(character)
        || char.GetUnicodeCategory(character) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Format;

    [Test]
    public void TheCatalogueCoversAllTwentyFiveLanguages()
    {
        PackLanguages().Count.ShouldBe(ExpectedPluginPacks);
        AllLanguages().Count.ShouldBe(ClarificationTexts.CoreLanguages.Count + ExpectedPluginPacks);
    }

    [Test]
    public void TheRequiredKeys_AreExactlyTheKeysTheCoreCatalogueCarries()
    {
        ClarificationTextKeys.RequiredKeys.Count.ShouldBe(ExpectedRequiredKeys);
        ClarificationTextKeys.RequiredKeys.Distinct(StringComparer.Ordinal).Count().ShouldBe(ExpectedRequiredKeys);
        ClarificationTexts.Keys.ShouldBe(ClarificationTextKeys.RequiredKeys, ignoreOrder: true);
        ClarificationTextKeys.RequiredKeys.ShouldNotContain(key => key.Contains(SkillSentenceMarker, StringComparison.Ordinal));
    }

    [Test]
    public void EveryLanguage_HasEveryKey_AndNoTextIsEmpty()
    {
        var problems = new List<string>();

        foreach (var language in AllLanguages())
        {
            foreach (var key in ClarificationTextKeys.RequiredKeys)
            {
                if (!ClarificationTexts.TryGetText(key, language, out var text) || string.IsNullOrWhiteSpace(text))
                {
                    problems.Add($"{language}: missing or empty '{key}'");
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
            foreach (var key in ClarificationTextKeys.RequiredKeys)
            {
                if (string.Equals(TextOf(key, language), English(key), StringComparison.Ordinal)
                    && !SameAsEnglishByDesign.ContainsKey((language, key)))
                {
                    problems.Add($"{language}: '{key}' equals the English text");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryPack_NamesTheAutonomyLevelAndTheKillSwitchAsItsOwnUiCallsThem()
    {
        var problems = new List<string>();

        foreach (var language in PackLanguages())
        {
            var translationsFile = Path.Combine(ApiRoot(), PluginsDirectory, LanguagesDirectory, language, TranslationsFileName);
            var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(translationsFile))!;
            var suggested = TextOf(ClarificationTextKeys.PlannerSuggested, language);

            foreach (var uiKey in new[] { AutonomyAssistedUiKey, KillSwitchUiKey })
            {
                if (!translations.TryGetValue(uiKey, out var uiTerm) || string.IsNullOrWhiteSpace(uiTerm))
                {
                    problems.Add($"{language}: {TranslationsFileName} has no '{uiKey}'");
                }
                else if (!suggested.Contains(uiTerm, StringComparison.Ordinal))
                {
                    problems.Add($"{language}: '{ClarificationTextKeys.PlannerSuggested}' does not contain the UI term '{uiTerm}' of '{uiKey}'");
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
            if (!string.Equals(TextOf(key, language), English(key), StringComparison.Ordinal))
            {
                problems.Add($"{language}: '{key}' no longer equals the English text - remove the exception ({reason})");
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void TheEnglishTexts_CarryExactlyThePlaceholdersTheCodeFills()
    {
        foreach (var key in ClarificationTextKeys.RequiredKeys)
        {
            var expected = ExpectedPlaceholders.GetValueOrDefault(key) ?? [];
            ClarificationTextTemplate.PlaceholdersOf(English(key)).ShouldBe(expected, ignoreOrder: true, customMessage: key);
        }
    }

    [Test]
    public void EveryLanguage_CarriesExactlyThePlaceholdersOfTheEnglishText()
    {
        var problems = new List<string>();

        foreach (var language in AllLanguages())
        {
            foreach (var key in ClarificationTextKeys.RequiredKeys)
            {
                var expected = ClarificationTextTemplate.PlaceholdersOf(English(key));
                var actual = ClarificationTextTemplate.PlaceholdersOf(TextOf(key, language));
                if (!expected.SetEquals(actual))
                {
                    problems.Add($"{language}: '{key}' has {{{string.Join(",", actual)}}}, expected {{{string.Join(",", expected)}}}");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void NoText_KeepsAStrayBraceOrARetiredPlaceholder()
    {
        var problems = new List<string>();
        var known = ClarificationTextTemplate.PlaceholdersOf(string.Join(' ', ClarificationTextKeys.RequiredKeys.Select(English)));

        foreach (var language in AllLanguages())
        {
            foreach (var key in ClarificationTextKeys.RequiredKeys)
            {
                var text = TextOf(key, language);
                if (text.Contains(RetiredLevelPlaceholder, StringComparison.Ordinal)
                    || text.Contains(RetiredKillSwitchPlaceholder, StringComparison.Ordinal))
                {
                    problems.Add($"{language}: '{key}' still carries {RetiredLevelPlaceholder} or {RetiredKillSwitchPlaceholder}");
                }

                var withoutPlaceholders = ClarificationTextTemplate.Render(text, known.ToDictionary(name => name, name => string.Empty));
                if (Regex.IsMatch(withoutPlaceholders, StrayBracePattern))
                {
                    problems.Add($"{language}: '{key}' keeps a stray brace");
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
            foreach (var key in ClarificationTextKeys.RequiredKeys)
            {
                var english = English(key);
                var text = TextOf(key, language);

                if (!EmojiSequence(text).SequenceEqual(EmojiSequence(english)))
                {
                    problems.Add($"{language}: '{key}' has a different emoji sequence");
                }

                if (Regex.Count(text, Regex.Escape(Bold)) != Regex.Count(english, Regex.Escape(Bold)))
                {
                    problems.Add($"{language}: '{key}' has a different number of {Bold}");
                }

                if (text.Count(c => c == LineBreak) != english.Count(c => c == LineBreak))
                {
                    problems.Add($"{language}: '{key}' has a different number of line breaks");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryLanguage_HasANeutralReplySubjectThatIsASafeSingleLineReply()
    {
        var problems = new List<string>();

        foreach (var language in AllLanguages())
        {
            var subject = TextOf(ClarificationTextKeys.MailNeutralReplySubject, language);

            if (!subject.StartsWith(InboundClarificationConstants.ReplySubjectPrefix, StringComparison.Ordinal))
            {
                problems.Add($"{language}: subject does not start with '{InboundClarificationConstants.ReplySubjectPrefix}'");
            }

            if (subject.Length > InboundClarificationConstants.MaxReplySubjectLength)
            {
                problems.Add($"{language}: subject is longer than {InboundClarificationConstants.MaxReplySubjectLength}");
            }

            if (subject.Any(IsForbiddenInHeader))
            {
                problems.Add($"{language}: subject contains a control, line-break or format character");
            }

            if (ClarificationTextTemplate.PlaceholdersOf(subject).Count > 0)
            {
                problems.Add($"{language}: subject carries a placeholder");
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void TheUnclearNotice_StartsWithItsWarningSign_BecauseTheCodeJoinsItByLineBreak()
    {
        foreach (var language in AllLanguages())
        {
            TextOf(ClarificationTextKeys.PlannerAnswerUnclearNotice, language).ShouldNotStartWith(LineBreak.ToString(), customMessage: language);
        }
    }

    [Test]
    public void AnUnknownLanguage_FallsBackToEnglish_ForEveryKey()
    {
        foreach (var key in ClarificationTextKeys.RequiredKeys)
        {
            ClarificationTexts.TryGetText(key, UnknownLanguage, out var text).ShouldBeTrue(key);
            text.ShouldBe(English(key));
        }

        ClarificationTexts.TryGetText(ClarificationTextKeys.PlannerStarted, null, out var withoutLanguage).ShouldBeTrue();
        withoutLanguage.ShouldBe(English(ClarificationTextKeys.PlannerStarted));
    }

    [Test]
    public void ARegionalTag_ReachesItsLanguage()
    {
        TextOf(ClarificationTextKeys.PlannerStarted, GermanRegional)
            .ShouldBe(ClarificationTexts.VariantsOf(ClarificationTextKeys.PlannerStarted)["de"]);
        TextOf(ClarificationTextKeys.PlannerStarted, PortugueseRegional)
            .ShouldBe(TextOf(ClarificationTextKeys.PlannerStarted, Portuguese));
        TextOf(ClarificationTextKeys.PlannerStarted, Portuguese).ShouldNotBe(English(ClarificationTextKeys.PlannerStarted));
    }

    [TestCase("zh-cn", ChineseSimplified)]
    [TestCase("ZH-CN", ChineseSimplified)]
    [TestCase("zh-tw", ChineseTraditional)]
    [TestCase(ChineseTraditional, ChineseTraditional)]
    public void TheChinesePacks_ResolveInAnyCasing_AndNeverToEnglish(string tag, string pack)
    {
        var text = TextOf(ClarificationTextKeys.PlannerStarted, tag);

        text.ShouldBe(TextOf(ClarificationTextKeys.PlannerStarted, pack));
        text.ShouldNotBe(English(ClarificationTextKeys.PlannerStarted));
    }

    [Test]
    public void TheTwoChinesePacks_DifferFromEachOther()
    {
        TextOf(ClarificationTextKeys.PlannerStarted, ChineseSimplified)
            .ShouldNotBe(TextOf(ClarificationTextKeys.PlannerStarted, ChineseTraditional));
    }

    [Test]
    public void AKnownLanguageWithAMissingKey_ResolvesToNothing_NotSilentlyToEnglish()
    {
        ClarificationTexts.Configure(ChineseSimplified, new Dictionary<string, string> { ["some.other.key"] = "x" });

        ClarificationTexts.TryGetText(ClarificationTextKeys.PlannerStarted, ChineseSimplified, out var text).ShouldBeFalse();
        text.ShouldBeEmpty();
    }

    [Test]
    public void ABlankPackText_CountsAsMissing()
    {
        ClarificationTexts.Configure(
            ChineseSimplified, new Dictionary<string, string> { [ClarificationTextKeys.PlannerStarted] = "   " });

        ClarificationTexts.TryGetText(ClarificationTextKeys.PlannerStarted, ChineseSimplified, out _).ShouldBeFalse();
    }
}
