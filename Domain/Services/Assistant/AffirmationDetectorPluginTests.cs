// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the plugin-language extension of AffirmationDetector — verifies that
/// affirmations and negations configured from conversation-signals.json are honoured for
/// space-segmented languages (token match, no substring false positives like "nie" inside
/// "niebieski") and for non-segmented scripts (substring match for Japanese/Chinese/Thai).
/// </summary>

using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class AffirmationDetectorPluginTests
{
    [OneTimeSetUp]
    public void ConfigurePluginEntries()
    {
        AffirmationDetector.Configure(
            affirmations: ["tak", "はい", "実行してください", "ใช่", "네", "de acuerdo"],
            negations: ["nie", "やめて", "ですか", "ไม่", "아니요"]);
    }

    [OneTimeTearDown]
    public void ResetPluginEntries()
    {
        AffirmationDetector.Reset();
    }

    [TestCase("tak")]
    [TestCase("Tak, wykonaj")]
    [TestCase("はい")]
    [TestCase("はい、実行してください")]
    [TestCase("ใช่")]
    [TestCase("네")]
    [TestCase("de acuerdo")]
    public void IsAffirmation_True_For_PluginLanguage_GoAheads(string message)
    {
        AffirmationDetector.IsAffirmation(message).ShouldBeTrue(message);
    }

    [TestCase("nie")]
    [TestCase("tak, ale nie teraz")]
    [TestCase("やめて")]
    [TestCase("はい、でもやめてください")]
    [TestCase("実行してもいいですか")]
    [TestCase("ไม่ใช่")]
    [TestCase("아니요")]
    public void IsAffirmation_False_When_PluginNegation_Present(string message)
    {
        AffirmationDetector.IsAffirmation(message).ShouldBeFalse(message);
    }

    [Test]
    public void PluginNegation_DoesNotMatch_As_Substring_In_SegmentedLanguage()
    {
        AffirmationDetector.IsAffirmation("tak, niebieski").ShouldBeTrue(
            "'nie' must only match as a whole token, not inside 'niebieski'");
    }

    [Test]
    public void PluginAffirmation_DoesNotMatch_As_Substring_In_SegmentedLanguage()
    {
        AffirmationDetector.IsAffirmation("taktisch gesehen schlecht").ShouldBeFalse(
            "'tak' must only match as a whole token, not inside 'taktisch'");
    }

    /// <summary>
    /// Red before the QuestionMarks fix, green after. "はい" is a configured plugin affirmation and
    /// nothing in these messages is a negation, so the only thing that can reject them is the question
    /// mark — and IsAffirmation tested ASCII '?' only, which U+FF1F is not. The confirmation gate this
    /// guards is reached in all 25 languages, so a CJK question was read as a go-ahead and the recipe
    /// proceeded on the scope the user was still asking about.
    /// </summary>
    [TestCase("はい、全員？")]
    [TestCase("実行してください？")]
    [TestCase("네？")]
    public void IsAffirmation_False_For_NonAsciiQuestionMarks(string message)
    {
        AffirmationDetector.IsAffirmation(message).ShouldBeFalse(message);
    }

    [Test]
    public void TheSameGoAhead_WithoutAQuestionMark_StaysAnAffirmation()
    {
        AffirmationDetector.IsAffirmation("はい、全員").ShouldBeTrue(
            "the question-mark test must reject the punctuation, not the language");
    }
}
