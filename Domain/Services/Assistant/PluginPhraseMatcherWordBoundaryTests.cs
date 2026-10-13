// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the one word-end rule shared by the refusal detector and the affirmation detector. The decision which
/// scripts keep prefix semantics (Han, Kana, Thai, Hangul) lives in PluginPhraseMatcher.IsAttachedSuffixScript and
/// the check that a segmented entry ends where the word ends in PluginPhraseMatcher.StartsWithEntryAtWordEnd; both
/// detectors call them, so the Korean "예" opening "예, 진행해" but not "예약 목록" and a combining mark that continues
/// a word are judged identically on both sides. Korean text is written as Unicode escapes.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class PluginPhraseMatcherWordBoundaryTests
{
    private const string Korean = "ko";
    private const string Spanish = "es";

    private const string KoYes = "예";
    private const string KoYesPolite = "좋아요";
    private const string KoReservation = "예약";
    private const string KoProceed = "진행해";
    private const string KoCancel = "취소";
    private const string KoCancelWithEnding = "취소해줘";
    private const string KoNo = "아니";
    private const string KoNoWithEnding = "아니야";
    private const string KoShow = "보여줘";
    private const string KoList = "목록";
    private const string JaNo = "いいえ";
    private const string ZhNo = "不";
    private const string ThNo = "ไม่";
    private const string HiNo = "नहीं";

    [SetUp]
    public void StartFromBareDetectors()
    {
        ShippedConversationSignals.Uninstall();
    }

    [TearDown]
    public void RestoreDetectorState()
    {
        ShippedConversationSignals.Uninstall();
    }

    [TestCase(KoCancel, true)]
    [TestCase(KoNo, true)]
    [TestCase(KoYes, true)]
    [TestCase(JaNo, true)]
    [TestCase(ZhNo, true)]
    [TestCase(ThNo, true)]
    [TestCase("no", false)]
    [TestCase("cancela", false)]
    [TestCase(HiNo, false)]
    [TestCase("d'accord", false)]
    public void IsAttachedSuffixScript_IsDecidedForHangulHanKanaAndThaiOnly(string entry, bool expected)
    {
        PluginPhraseMatcher.IsAttachedSuffixScript(entry).ShouldBe(expected);
    }

    [TestCase(KoCancel, KoCancelWithEnding, true)]
    [TestCase(KoNo, KoNoWithEnding, true)]
    [TestCase("no", "no, gracias", true)]
    [TestCase("no", "nombre del cliente", false)]
    [TestCase("nope!", "nope!mejor", true)]
    [TestCase("ne", "ne̸ctar", false)]
    public void StartsWithEntryAtWordEnd_AppliesTheSameRuleToEveryScript(string entry, string message, bool expected)
    {
        PluginPhraseMatcher.StartsWithEntryAtWordEnd(message, entry).ShouldBe(expected);
    }

    [Test]
    public void ASpacingCombiningMark_ContinuesTheWordLikeALetter()
    {
        var bengaliNo = "না";

        PluginPhraseMatcher.StartsWithEntryAtWordEnd(bengaliNo + "রা", bengaliNo).ShouldBeFalse();
        PluginPhraseMatcher.StartsWithEntryAtWordEnd(bengaliNo + ", ধন্য", bengaliNo).ShouldBeTrue();
        PluginPhraseMatcher.StartsWithEntryAtWordEnd("aः", "a").ShouldBeFalse();
    }

    [TestCase(KoYes + ", " + KoProceed, true)]
    [TestCase(KoYes + " " + KoProceed, true)]
    [TestCase(KoYesPolite, true)]
    [TestCase(KoYesPolite + ", " + KoProceed, true)]
    [TestCase(KoReservation + " " + KoList + " " + KoShow, false)]
    [TestCase(KoReservation, false)]
    [TestCase(KoReservation + " " + "\uB124", false)]
    public void Korean_LeadsWithAffirmation_JudgesTheWordEndLikeTheRefusalDetector(string message, bool expected)
    {
        ShippedConversationSignals.Install();

        AffirmationDetector.LeadsWithAffirmation(message, Korean).ShouldBe(expected, message);
    }

    [TestCase(KoNo + KoShow, true)]
    [TestCase(KoCancelWithEnding, true)]
    [TestCase(KoReservation + " " + KoList, false)]
    public void Korean_LeadsWithNegation_KeepsThePrefixForAttachedEndings(string message, bool expected)
    {
        ShippedConversationSignals.Install();

        DeclineDetector.LeadsWithNegation(message, Korean).ShouldBe(expected, message);
    }

    [Test]
    public void Spanish_ACancelOrder_IsARefusalOfThePendingAction_NotAnAffirmation()
    {
        ShippedConversationSignals.Install();

        DeclineDetector.LeadsWithNegation("Cancélalo", Spanish).ShouldBeTrue();
        DeclineDetector.LeadsWithNegation("Cancelalo", Spanish).ShouldBeTrue();
        AffirmationDetector.IsAffirmation("Vale, cancélalo", Spanish).ShouldBeFalse();
    }

    [Test]
    public void Spanish_APlainRequestThatStartsLikeARefusalWord_IsNeitherARefusalNorAnAffirmation()
    {
        ShippedConversationSignals.Install();

        DeclineDetector.LeadsWithNegation("Nombre del cliente", Spanish).ShouldBeFalse();
        DeclineDetector.LeadsWithNegation("Cancelaciones de la semana", Spanish).ShouldBeFalse();
        AffirmationDetector.LeadsWithAffirmation("Siempre el turno de noche", Spanish).ShouldBeFalse();
    }
}
