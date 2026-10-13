// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The detectors against the REAL language packs for three decisions of the collision table. A UI language that knows a
/// colliding word only inside its own phrases (Danish "nej tak", "ikke nu, tak") reads the bare word as no yes/no signal
/// and lets the rest of the message decide; Japanese "大丈夫です" is a refusal only, never also a yes, so "今は大丈夫です"
/// refuses again; and a decomposed (NFD) reply is composed before it is read, like the vocabulary entries.
/// </summary>

using System.Text;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class AffirmationDetectorNeutralWordTests
{
    [OneTimeSetUp]
    public void InstallThePacks() => ShippedConversationSignals.Install();

    [OneTimeTearDown]
    public void UninstallThePacks() => ShippedConversationSignals.Uninstall();

    [TestCase("Ja tak", "da", ConfirmationVerdict.Confirmed)]
    [TestCase("ja, tak", "da", ConfirmationVerdict.Confirmed)]
    [TestCase("Nej tak", "da", ConfirmationVerdict.NotConfirmed)]
    [TestCase("Nej, tak", "da", ConfirmationVerdict.NotConfirmed)]
    [TestCase("ikke nu, tak", "da", ConfirmationVerdict.NotConfirmed)]
    [TestCase("tak", "da", ConfirmationVerdict.Ambiguous)]
    [TestCase("Tak.", "da-DK", ConfirmationVerdict.Ambiguous)]
    [TestCase("tak", "pl", ConfirmationVerdict.Confirmed)]
    [TestCase("tak", "ms", ConfirmationVerdict.NotConfirmed)]
    [TestCase("tak", "de", ConfirmationVerdict.Ambiguous)]
    [TestCase("tak", "cs", ConfirmationVerdict.Ambiguous)]
    [TestCase("tak", null, ConfirmationVerdict.Ambiguous)]
    public void AssessAffirmation_TheDanishTakIsNoSignalOfItsOwn_TheRestOfTheMessageDecides(
        string message, string? language, ConfirmationVerdict expected)
    {
        AffirmationDetector.AssessAffirmation(message, language).Verdict.ShouldBe(expected, $"'{message}' in '{language}'");
    }

    [TestCase("Ja tak", "da", ConfirmationVerdict.Confirmed)]
    [TestCase("Nej tak", "da", ConfirmationVerdict.NotConfirmed)]
    [TestCase("tak", "da", ConfirmationVerdict.Ambiguous)]
    [TestCase("tak", "pl", ConfirmationVerdict.Confirmed)]
    [TestCase("tak", "ms", ConfirmationVerdict.NotConfirmed)]
    [TestCase("tak", "de", ConfirmationVerdict.Ambiguous)]
    public void AssessExplicitConfirmation_FollowsTheSameDecision(string message, string? language, ConfirmationVerdict expected)
    {
        AffirmationDetector.AssessExplicitConfirmation(message, language).Verdict.ShouldBe(expected, $"'{message}' in '{language}'");
        AffirmationDetector.IsExplicitConfirmation(message, language).ShouldBe(expected == ConfirmationVerdict.Confirmed);
    }

    [Test]
    public void ABareDanishTak_IsAmbiguousAndNamesOnlyTheLanguagesThatReallyDisagree()
    {
        var assessment = AffirmationDetector.AssessAffirmation("tak", "da");

        assessment.IsAmbiguous.ShouldBeTrue();
        assessment.AmbiguousWord.ShouldBe("tak");
        assessment.AmbiguousLanguages.ShouldBe(["ms", "pl"]);
        AffirmationDetector.AssessAffirmation("tak", "de").AmbiguousLanguages.ShouldBe(["ms", "pl"]);
    }

    [TestCase("Nej tak", "da", true)]
    [TestCase("ikke nu, tak", "da", true)]
    [TestCase("Ja tak", "da", false)]
    [TestCase("tak", "da", false)]
    [TestCase("tak", "ms", true)]
    [TestCase("tak", "pl", false)]
    [TestCase("tak", "de", false)]
    public void DeclineDetector_ADanishTakIsNeverARefusalOfItsOwn_OnlyTheDanishPhrasesAre(
        string message, string? language, bool refusal)
    {
        DeclineDetector.LeadsWithNegation(message, language).ShouldBe(refusal, $"'{message}' in '{language}'");
    }

    [TestCase("今は大丈夫です")]
    [TestCase("大丈夫です")]
    [TestCase("結構です")]
    public void Japanese_DaijobuDesuAndItsPhrases_AreRefusals(string message)
    {
        DeclineDetector.LeadsWithNegation(message, "ja").ShouldBeTrue(message);
        DeclineDetector.IsBareNegation(message, "ja").ShouldBeTrue(message);
        AffirmationDetector.AssessAffirmation(message, "ja").Verdict.ShouldBe(ConfirmationVerdict.NotConfirmed, message);
        AffirmationDetector.IsExplicitConfirmation(message, "ja").ShouldBeFalse(message);
    }

    [TestCase("はい")]
    [TestCase("いいですよ")]
    [TestCase("お願いします")]
    public void Japanese_TheRemainingYesEntries_StillConfirm(string message)
    {
        AffirmationDetector.IsExplicitConfirmation(message, "ja").ShouldBeTrue(message);
        DeclineDetector.LeadsWithNegation(message, "ja").ShouldBeFalse(message);
    }

    [Test]
    public void ADecomposedPolishAffirmationPhrase_IsAYes_WhereTheComposedFormIs()
    {
        var composed = "zrób to";
        var decomposed = composed.Normalize(NormalizationForm.FormD);

        decomposed.ShouldNotBe(composed);
        AffirmationDetector.IsAffirmation(composed, "pl").ShouldBeTrue();
        AffirmationDetector.IsAffirmation(decomposed, "pl").ShouldBeTrue();
        AffirmationDetector.IsExplicitConfirmation(decomposed, "pl").ShouldBeTrue();
    }

    [Test]
    public void ADecomposedJapaneseNegation_StillVetoesTheYesInFrontOfIt()
    {
        var composed = "はい、だめ";
        var decomposed = composed.Normalize(NormalizationForm.FormD);

        decomposed.ShouldNotBe(composed);
        AffirmationDetector.AssessAffirmation(composed, "ja").Verdict.ShouldBe(ConfirmationVerdict.NotConfirmed);
        AffirmationDetector.AssessAffirmation(decomposed, "ja").Verdict.ShouldBe(ConfirmationVerdict.NotConfirmed);
    }

    [Test]
    public void AMalformedSurrogateReply_CostsADetectionNotTheTurn()
    {
        var malformed = "tak \uD800";

        Should.NotThrow(() => AffirmationDetector.AssessAffirmation(malformed, "pl"));
    }
}
