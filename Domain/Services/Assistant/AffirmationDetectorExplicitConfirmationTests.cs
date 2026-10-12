// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The server-side "real yes" behind a held action. IsAffirmation alone accepts any message that carries an
/// affirmation or courtesy token anywhere ("Zeig mir die Dienste bitte"), which is far too loose to decide whether the
/// user answered the question; IsExplicitConfirmation additionally demands that the reply opens with the yes (leading
/// courtesy words skipped) or is a very short reply. The multilingual cases run against the REAL conversation-signals
/// files of all 21 language plugins plus the four core languages.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class AffirmationDetectorExplicitConfirmationTests
{
    private const string LanguagesDirectory = "Languages";
    private const string SignalsFileName = "conversation-signals.json";
    private const string AffirmationsProperty = "affirmations";
    private const string NegationsProperty = "negations";

    private static readonly List<string> PluginAffirmations = [];
    private static readonly List<string> PluginNegations = [];

    private static readonly HashSet<(string Language, string Entry)> KnownCrossLanguageCollisions =
        [("pl", "tak")];

    private const int MinimumShippedLanguagePacks = 21;

    private static IReadOnlyList<(string Language, string Entry)> AffirmationEntriesPerLanguage = [];
    private static int LanguagePacksWithSignals;

    [OneTimeSetUp]
    public void LoadAllLanguagePacks()
    {
        var languages = Path.Combine(
            RepositoryRootLocator.RequireRoot(), "Klacks.Api", "Plugins", LanguagesDirectory);
        var entries = new List<(string, string)>();
        LanguagePacksWithSignals = 0;

        foreach (var directory in Directory.GetDirectories(languages))
        {
            var file = Path.Combine(directory, SignalsFileName);
            if (!File.Exists(file))
            {
                continue;
            }

            LanguagePacksWithSignals++;

            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var affirmations = document.RootElement.GetProperty(AffirmationsProperty)
                .EnumerateArray().Select(item => item.GetString()!).ToList();
            var negations = document.RootElement.GetProperty(NegationsProperty)
                .EnumerateArray().Select(item => item.GetString()!).ToList();

            PluginAffirmations.AddRange(affirmations);
            PluginNegations.AddRange(negations);
            entries.AddRange(affirmations.Select(entry => (Path.GetFileName(directory), entry)));
        }

        AffirmationEntriesPerLanguage = entries;
        AffirmationDetector.Configure(PluginAffirmations, PluginNegations);
    }

    [OneTimeTearDown]
    public void ResetPluginEntries()
    {
        AffirmationDetector.Reset();
        PluginAffirmations.Clear();
        PluginNegations.Clear();
    }

    [TestCase("ja")]
    [TestCase("Ja")]
    [TestCase("Ja bitte")]
    [TestCase("Ja, ausführen")]
    [TestCase("Bitte ausführen")]
    [TestCase("ok")]
    [TestCase("mach das")]
    [TestCase("genau, passt")]
    [TestCase("Alles klar")]
    [TestCase("Das passt")]
    [TestCase("yes")]
    [TestCase("Yes please")]
    [TestCase("Please proceed")]
    [TestCase("Please do it")]
    [TestCase("go ahead")]
    [TestCase("do it")]
    [TestCase("oui")]
    [TestCase("Oui, vas-y")]
    [TestCase("sì")]
    [TestCase("certo")]
    public void IsExplicitConfirmation_True_ForARealYes(string message)
    {
        AffirmationDetector.IsExplicitConfirmation(message).ShouldBeTrue(message);
    }

    [TestCase("Zeig mir die Dienste bitte")]
    [TestCase("Neuen Mitarbeiter, bitte")]
    [TestCase("Bitte lege einen neuen Dienst für Montag an")]
    [TestCase("Lösche die Adresse von Müller, das ist sicher richtig so")]
    [TestCase("Please show me the shifts for next week")]
    [TestCase("Show the schedule, it is correct that way")]
    [TestCase("bitte")]
    [TestCase("please")]
    [TestCase("")]
    [TestCase("   ")]
    public void IsExplicitConfirmation_False_ForARequestThatMerelyContainsACourtesyOrFillerWord(string message)
    {
        AffirmationDetector.IsExplicitConfirmation(message).ShouldBeFalse(message);
    }

    [TestCase("nein")]
    [TestCase("ja, aber nicht heute")]
    [TestCase("no, cancel")]
    [TestCase("ja? was kostet das")]
    [TestCase("ok, aber wie viele sind das?")]
    [TestCase("stop")]
    public void IsExplicitConfirmation_False_WhenTheReplyIsNegatedOrAQuestion(string message)
    {
        AffirmationDetector.IsExplicitConfirmation(message).ShouldBeFalse(message);
    }

    [Test]
    public void IsExplicitConfirmation_False_ForNull()
    {
        AffirmationDetector.IsExplicitConfirmation(null).ShouldBeFalse();
    }

    [Test]
    public void IsExplicitConfirmation_False_ForALongRequestWhoseOnlyAffirmationWordSitsInTheMiddle()
    {
        AffirmationDetector.IsExplicitConfirmation(
            "Lege für Montag und Dienstag bitte je einen neuen Frühdienst im Lager an").ShouldBeFalse();
    }

    [TestCase("はい、実行してください")]
    [TestCase("はい")]
    [TestCase("是的")]
    [TestCase("好的，继续")]
    [TestCase("네")]
    [TestCase("ใช่")]
    public void IsExplicitConfirmation_True_ForANonSegmentedScriptReplyThatOpensWithTheYes(string message)
    {
        AffirmationDetector.IsExplicitConfirmation(message).ShouldBeTrue(message);
    }

    [TestCase("新しい勤務を月曜日に追加してください、大丈夫です")]
    [TestCase("请帮我在星期一新增一个班次，可以")]
    public void IsExplicitConfirmation_False_ForANonSegmentedScriptRequestThatOnlyContainsTheYesLater(string message)
    {
        AffirmationDetector.IsExplicitConfirmation(message).ShouldBeFalse(message);
    }

    [TestCase("sim, pode fazer")]
    [TestCase("dobrze, wykonaj")]
    [TestCase("vale")]
    [TestCase("de acuerdo, adelante")]
    [TestCase("ya, lanjutkan")]
    [TestCase("kyllä")]
    public void IsExplicitConfirmation_True_ForAPluginLanguageReplyThatOpensWithTheYes(string message)
    {
        AffirmationDetector.IsExplicitConfirmation(message).ShouldBeTrue(message);
    }

    [Test]
    public void IsExplicitConfirmation_True_ForEveryAffirmationEntryOfEveryLanguagePackOnItsOwn()
    {
        AffirmationEntriesPerLanguage.ShouldNotBeEmpty();

        var rejected = AffirmationEntriesPerLanguage
            .Where(item => !KnownCrossLanguageCollisions.Contains((item.Language, item.Entry)))
            .Where(item => !AffirmationDetector.IsExplicitConfirmation(item.Entry))
            .Select(item => $"{item.Language}: '{item.Entry}'")
            .ToList();

        rejected.ShouldBeEmpty(
            "A language pack's own go-ahead word must be accepted as a yes on its own, otherwise users of that " +
            "language can never confirm a held action: " + string.Join("; ", rejected));
    }

    [Test]
    public void KnownCrossLanguageCollisions_AreTheOnlyAffirmationsAnotherPackNegates()
    {
        var negations = PluginNegations.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var collisions = AffirmationEntriesPerLanguage
            .Where(item => negations.Contains(item.Entry))
            .Select(item => (item.Language, item.Entry))
            .ToHashSet();

        collisions.ShouldBe(KnownCrossLanguageCollisions, ignoreOrder: true,
            "The detectors merge every installed pack into one global vocabulary, so a go-ahead word of one pack " +
            "that another pack lists as a negation is never accepted as a yes while both packs are installed. " +
            "Update this list deliberately when a pack changes.");
    }

    [Test]
    public void ThePolishTak_IsRefusedWhileTheMalayPackNegatesIt_AndStillAccepted_ByItsOtherPolishGoAheads()
    {
        AffirmationDetector.IsExplicitConfirmation("tak").ShouldBeFalse();
        AffirmationDetector.IsExplicitConfirmation("dobrze").ShouldBeTrue();
        AffirmationDetector.IsExplicitConfirmation("zgoda").ShouldBeTrue();
    }

    [Test]
    public void IsExplicitConfirmation_CoversEveryLanguagePackOnDisk()
    {
        var covered = AffirmationEntriesPerLanguage.Select(item => item.Language).Distinct().Count();

        covered.ShouldBe(LanguagePacksWithSignals);
        covered.ShouldBeGreaterThanOrEqualTo(MinimumShippedLanguagePacks);
    }

    [TestCase("Mach einen Dienst für Montag")]
    [TestCase("Go to the schedule")]
    [TestCase("Weiter zum nächsten")]
    [TestCase("Los turnos de Pérez")]
    [TestCase("Kör autofyll")]
    [TestCase("Si Pérez falta")]
    [TestCase("Lösche den Dienst, los")]
    [TestCase("Continue with the next employee in the list")]
    [TestCase("Go for Monday")]
    [TestCase("Claro que Pérez falta")]
    public void IsExplicitConfirmation_False_ForARequestThatOpensWithAnImperativeOrHomograph(string message)
    {
        AffirmationDetector.IsExplicitConfirmation(message).ShouldBeFalse(message);
    }

    [TestCase("ja, aber nur für Montag")]
    [TestCase("Yes, but only the first one")]
    [TestCase("Oui, mais seulement lundi")]
    [TestCase("Sì, ma solo lunedì")]
    [TestCase("ok, zuerst den Frühdienst")]
    [TestCase("Yes, except Monday")]
    [TestCase("Ja, ausser Pérez")]
    [TestCase("Ja, wenn es geht")]
    public void IsExplicitConfirmation_False_WhenTheYesCarriesAConditionalMarker(string message)
    {
        AffirmationDetector.IsExplicitConfirmation(message).ShouldBeFalse(message);
    }

    [TestCase("Sí, borra el turno")]
    [TestCase("D'accord")]
    [TestCase("d’accord, vas-y")]
    [TestCase("Ähm, ja, bitte mach das")]
    [TestCase("Äh, ok")]
    [TestCase("Hmm, genau")]
    [TestCase("ok mach")]
    [TestCase("yes please do it")]
    [TestCase("Ja bitte")]
    [TestCase("Si, procedi")]
    [TestCase("si")]
    [TestCase("mach das")]
    [TestCase("do it")]
    [TestCase("los, ausführen")]
    [TestCase("Go for it")]
    [TestCase("Let's do it")]
    [TestCase("Claro que sí")]
    [TestCase("Claro que sim")]
    public void IsExplicitConfirmation_True_ForARealYesIncludingFillersApostrophesAndHomographsSetOffByPunctuation(string message)
    {
        AffirmationDetector.IsExplicitConfirmation(message).ShouldBeTrue(message);
    }

    [TestCase("Kör, autofyll")]
    [TestCase("Kör.")]
    [TestCase("Kör autofyll nu")]
    public void IsExplicitConfirmation_TreatsAPluginEntryAsAnOpenerOnlyWhenPunctuationOrTheEndFollows(string message)
    {
        var isYes = AffirmationDetector.IsExplicitConfirmation(message);

        isYes.ShouldBe(message is "Kör, autofyll" or "Kör.");
    }

    [TestCase("kör autofyll")]
    [TestCase("ya lanjutkan")]
    public void IsExplicitConfirmation_ARunOnPluginEntry_IsAYesOnlyAsAShortReplyMadeOfVocabulary(string message)
    {
        var isYes = AffirmationDetector.IsExplicitConfirmation(message);

        isYes.ShouldBe(message == "ya lanjutkan");
    }

    [TestCase("可以新增一个班次")]
    [TestCase("好的继续")]
    [TestCase("是的请继续做别的")]
    public void IsExplicitConfirmation_ANonSegmentedReplyWithoutPunctuation_NeedsToBeShortAndMadeOfPackEntries(string message)
    {
        var isYes = AffirmationDetector.IsExplicitConfirmation(message);

        isYes.ShouldBe(message == "好的继续");
    }

    [Test]
    public void LeadsWithAffirmation_UsesAWordBoundaryForPluginEntries_LikeIsExplicitConfirmation()
    {
        AffirmationDetector.LeadsWithAffirmation("ya, lanjutkan").ShouldBeTrue();
        AffirmationDetector.LeadsWithAffirmation("yank the cable, lanjutkan").ShouldBeFalse();
        AffirmationDetector.LeadsWithAffirmation("はい、全員").ShouldBeTrue();
        AffirmationDetector.LeadsWithAffirmation("Neuen Mitarbeiter, bitte").ShouldBeFalse();
    }

    [Test]
    public void NoWordOfAnyPackAffirmationEntry_IsAConditionalMarker_SoAPackYesIsNeverTurnedIntoAConditionalOne()
    {
        var clashes = AffirmationEntriesPerLanguage
            .SelectMany(item => item.Entry.Split(' ').Select(word => (item.Language, Word: word.ToLowerInvariant())))
            .Where(item => AffirmationVocabulary.ConditionalWords.Contains(item.Word))
            .Select(item => $"{item.Language}: '{item.Word}'")
            .ToList();

        clashes.ShouldBeEmpty(string.Join("; ", clashes));
    }

    [Test]
    public void NoCoreReplyWord_IsAConditionalMarkerOrANegation()
    {
        AffirmationVocabulary.ReplyWords.Intersect(AffirmationVocabulary.ConditionalWords).ShouldBeEmpty();
        AffirmationVocabulary.ReplyWords.Intersect(AffirmationVocabulary.NegationWords).ShouldBeEmpty();
        AffirmationVocabulary.GoAheadWords.Intersect(AffirmationVocabulary.ConditionalWords).ShouldBeEmpty();
        AffirmationVocabulary.GoAheadWords.Intersect(AffirmationVocabulary.NegationWords).ShouldBeEmpty();
    }

    [Test]
    public void IsAffirmation_ReadsAnApostropheInsideAWordAsOneWord()
    {
        AffirmationDetector.IsAffirmation("d'accord").ShouldBeTrue();
        AffirmationDetector.IsAffirmation("oui, don't do that").ShouldBeFalse();
    }
}
