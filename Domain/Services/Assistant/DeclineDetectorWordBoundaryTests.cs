// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the word boundary of the leading-refusal match. A pack entry used to be matched with a bare
/// StartsWith, so the Malay refusal "tak" read "Take the first shift" as a refusal and the Spanish "no" read
/// "Nombre del cliente" as one, and the recipe engine then dropped the semantic fallback for a genuine request.
/// A segmented-script entry now has to end where the word ends; entries of scripts whose words carry attached
/// suffixes or have no separators at all (Han, Kana, Thai, Hangul) keep matching as a prefix. The shipped
/// packs are checked as a whole: no single-word entry may fire on a word that merely starts with it.
/// </summary>

using System.Globalization;
using System.Text.Json;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class DeclineDetectorWordBoundaryTests
{
    private const string ConversationSignalsFileName = "conversation-signals.json";
    private const string NegationsKey = "negations";
    private const string DeclinesKey = "declines";
    private const string WordContinuation = "zq";
    private const string WordInternalPunctuation = "'’-";

    private static readonly string[] PluginsLanguagesRelativePath = { "Klacks.Api", "Plugins", "Languages" };

    [SetUp]
    public void StartFromBareDetectors()
    {
        ShippedConversationSignals.Uninstall();
    }

    [TearDown]
    public void RestoreDetectorState()
    {
        ShippedConversationSignals.Uninstall();
        DeclineDetector.ResetCore();
        ConversationSignalsPluginLoader.LoadCore(AppContext.BaseDirectory);
    }

    [TestCase("tak", "Take the first shift")]
    [TestCase("tak", "Takeover of the night shift")]
    [TestCase("no", "Nombre del cliente")]
    [TestCase("no", "Normal ist das nicht")]
    [TestCase("nie", "Niebieski Dienst")]
    [TestCase("ei", "Eilen tein vuoron")]
    [TestCase("ne", "Nemocnice v Praze")]
    [TestCase("lo", "Lorem ipsum")]
    public void ASingleWordEntry_DoesNotFireOnAWordThatMerelyStartsWithIt(string entry, string message)
    {
        DeclineDetector.Configure([entry], []);

        DeclineDetector.LeadsWithNegation(message).ShouldBeFalse();
    }

    [TestCase("tak", "Tak")]
    [TestCase("tak", "Tak.")]
    [TestCase("tak", "Tak, dziękuję")]
    [TestCase("tak", "tak - nein")]
    [TestCase("no", "No")]
    [TestCase("no", "No, gracias")]
    [TestCase("no", "No. Mejor otro día")]
    [TestCase("no", "No-no")]
    [TestCase("no", "¡No!")]
    public void ASingleWordEntry_StillFiresOnTheWholeWord(string entry, string message)
    {
        DeclineDetector.Configure([entry], []);

        DeclineDetector.LeadsWithNegation(message).ShouldBeTrue();
    }

    [TestCase("mas tarde", "Mas tarde")]
    [TestCase("mas tarde", "Mas tarde, gracias")]
    [TestCase("mas tarde", "Mas tarde!")]
    [TestCase("ahora no", "Ahora no, gracias.")]
    public void AMultiWordPhrase_FiresWhenItEndsAtAWordBoundary(string phrase, string message)
    {
        DeclineDetector.Configure([], [phrase]);

        DeclineDetector.LeadsWithNegation(message).ShouldBeTrue();
    }

    [TestCase("mas tarde", "Mas tardecito")]
    [TestCase("ahora no", "Ahora nombre del cliente")]
    public void AMultiWordPhrase_DoesNotFireWhenTheLastWordContinues(string phrase, string message)
    {
        DeclineDetector.Configure([], [phrase]);

        DeclineDetector.LeadsWithNegation(message).ShouldBeFalse();
    }

    [TestCase("いいえ", "いいえ、結構です")]
    [TestCase("いいえ", "いいえ")]
    [TestCase("いいえ", "いいえ今はいいです")]
    [TestCase("不要了", "不要了谢谢")]
    [TestCase("不", "不，所有员工。")]
    [TestCase("ไม่", "ไม่เอาครับ")]
    [TestCase("취소", "취소해줘")]
    [TestCase("아니", "아니에요")]
    [TestCase("아니", "아니야")]
    public void AnEntryOfAScriptWithAttachedSuffixes_KeepsMatchingAsAPrefix(string entry, string message)
    {
        DeclineDetector.Configure([entry], []);

        DeclineDetector.LeadsWithNegation(message).ShouldBeTrue();
    }

    [Test]
    public void AnEntryEndingInAPunctuationMark_NeedsNoWordBoundaryAfterIt()
    {
        DeclineDetector.Configure([], ["nope!"]);

        DeclineDetector.LeadsWithNegation("Nope!Mejor otro").ShouldBeTrue();
    }

    [Test]
    public void AWordContinuedByACombiningMark_IsNotTheEntry()
    {
        DeclineDetector.Configure(["ne"], []);

        DeclineDetector.LeadsWithNegation("ne̸ctar bitte").ShouldBeFalse();
        DeclineDetector.LeadsWithNegation("ne̸ bitte").ShouldBeFalse();
        DeclineDetector.LeadsWithNegation("ne, danke").ShouldBeTrue();
    }

    [TestCase("Take the first shift", "en")]
    [TestCase("Take the first shift", null)]
    [TestCase("Takeover for Monday please", "en")]
    [TestCase("Nombre del cliente", "es")]
    [TestCase("Nombre del cliente", null)]
    [TestCase("Normalmente trabaja de noche", "es")]
    [TestCase("Nemocnice v Praze", "cs")]
    [TestCase("Eilen tein vuoron", "fi")]
    [TestCase("Niebieski kolor", "pl")]
    [TestCase("לאן הוא הלך", "he")]
    [TestCase("لائحة الموظفين", "ar")]
    public void ShippedPacks_GenuineRequestsThatStartLikeARefusalWord_AreNoRefusal(string message, string? language)
    {
        ShippedConversationSignals.Install();

        DeclineDetector.LeadsWithNegation(message, language).ShouldBeFalse();
    }

    [TestCase("No", "es")]
    [TestCase("No, gracias", "es")]
    [TestCase("No gracias", "es")]
    [TestCase("Cancélalo", "es")]
    [TestCase("Cancelalo, por favor", "es")]
    [TestCase("Cancélala", "es")]
    [TestCase("Deténlo", "es")]
    [TestCase("Nein danke", "de")]
    [TestCase("Nein", "de")]
    [TestCase("Tak, dziękuję", "ms")]
    [TestCase("tak", "ms")]
    [TestCase("Non merci", "fr")]
    [TestCase("いいえ", "ja")]
    [TestCase("いいえ、結構です", "ja")]
    [TestCase("아니에요", "ko")]
    [TestCase("ไม่เอาครับ", "th")]
    [TestCase("Nie, dziękuję", "pl")]
    [TestCase("Ne, děkuji", "cs")]
    [TestCase("Ei kiitos", "fi")]
    public void ShippedPacks_RealRefusals_StillLeadWithNegation(string message, string language)
    {
        ShippedConversationSignals.Install();

        DeclineDetector.LeadsWithNegation(message, language).ShouldBeTrue();
    }

    [Test]
    public void ShippedPacks_APolishYes_IsNoRefusalInAPolishUi_ButTheMalayNoStillIs()
    {
        ShippedConversationSignals.Install();

        DeclineDetector.LeadsWithNegation("Tak, dziękuję", "pl").ShouldBeFalse();
        DeclineDetector.LeadsWithNegation("Tak, terima kasih", "ms").ShouldBeTrue();
        DeclineDetector.LeadsWithNegation("Take the first shift", "ms").ShouldBeFalse();
    }

    [Test]
    public void EveryShippedSingleWordEntry_DoesNotFireOnAWordThatMerelyStartsWithIt()
    {
        var violations = new List<string>();

        foreach (var packDir in PackDirectories())
        {
            var code = new DirectoryInfo(packDir).Name;
            var signals = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
                File.ReadAllText(Path.Combine(packDir, ConversationSignalsFileName)))!;
            var negations = signals.GetValueOrDefault(NegationsKey) ?? [];
            var declines = signals.GetValueOrDefault(DeclinesKey) ?? [];

            DeclineDetector.Reset();
            DeclineDetector.ResetCore();
            DeclineDetector.Configure(negations, declines);

            foreach (var entry in negations.Concat(declines).Where(IsSegmentedWordEntry))
            {
                var message = entry + WordContinuation + " rest";
                if (DeclineDetector.LeadsWithNegation(message))
                {
                    violations.Add($"{code}: '{message}' is read as a refusal because of the entry '{entry}'");
                }
            }

            DeclineDetector.Reset();
        }

        violations.ShouldBeEmpty(string.Join("\n", violations));
    }

    private static bool IsSegmentedWordEntry(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry) || PluginPhraseMatcher.IsAttachedSuffixScript(entry))
        {
            return false;
        }

        return IsWordLetterOrMark(entry[^1])
            && entry.All(value => IsWordLetterOrMark(value) || WordInternalPunctuation.Contains(value));
    }

    private static bool IsWordLetterOrMark(char value) =>
        char.IsLetter(value)
        || CharUnicodeInfo.GetUnicodeCategory(value) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;

    private static IEnumerable<string> PackDirectories() =>
        Directory.GetDirectories(RepositoryRootLocator.RequireDirectory(PluginsLanguagesRelativePath))
            .Where(dir => File.Exists(Path.Combine(dir, ConversationSignalsFileName)))
            .OrderBy(dir => dir, StringComparer.Ordinal);
}
