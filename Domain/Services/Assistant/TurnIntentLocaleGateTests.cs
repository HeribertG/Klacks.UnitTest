// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Models.Assistant.TurnIntent;
using Klacks.UnitTest.Application.Services.Assistant.TurnIntent;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class TurnIntentLocaleGateTests
{
    [Test]
    public void WilsonLowerBound_MatchesTheReferenceValue()
    {
        TurnIntentLocaleGate.WilsonLowerBound(48, 50).ShouldBe(0.865, 0.001);
        TurnIntentLocaleGate.WilsonLowerBound(0, 0).ShouldBe(0.0);
    }

    [Test]
    public void ShowsImprovement_WhenPairsAndAskPrecisionClearTheirBounds()
    {
        TurnIntentLocaleGate.ShowsImprovement(Verdict(pairs: 40, pairsCorrect: 38, detectorPairs: 10,
            askPredicted: 30, askCorrect: 30)).ShouldBeTrue();
    }

    [Test]
    public void ShowsNoImprovement_WithTooFewPairs()
    {
        TurnIntentLocaleGate.ShowsImprovement(Verdict(pairs: 4, pairsCorrect: 4, detectorPairs: 0,
            askPredicted: 30, askCorrect: 30)).ShouldBeFalse();
    }

    [Test]
    public void ShowsNoImprovement_WhenThePairLowerBoundDoesNotBeatTheDetector()
    {
        TurnIntentLocaleGate.ShowsImprovement(Verdict(pairs: 40, pairsCorrect: 34, detectorPairs: 30,
            askPredicted: 30, askCorrect: 30)).ShouldBeFalse();
    }

    [Test]
    public void ShowsNoImprovement_WhenConfidentAskPrecisionIsTooUncertain()
    {
        TurnIntentLocaleGate.ShowsImprovement(Verdict(pairs: 40, pairsCorrect: 40, detectorPairs: 0,
            askPredicted: 20, askCorrect: 17)).ShouldBeFalse();
    }

    [TestCase("zh-TW", "zh-TW")]
    [TestCase("DE", "de")]
    [TestCase("de-CH", "de")]
    [TestCase("pt-BR", "pt")]
    public void ResolveLocale_MatchesExactTagThenPrimarySubtag(string language, string expected)
    {
        var locales = new[] { "de", "pt", "zh-TW", "zh-CN" }
            .Select(l => TurnIntentHeadTestFactory.Verdict(l, true)).ToList();

        TurnIntentLocaleGate.ResolveLocale(language, locales)!.Locale.ShouldBe(expected);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("ko")]
    [TestCase("zh-HK")]
    public void ResolveLocale_IsNullForUnknownLanguages(string? language)
    {
        var locales = new[] { "de", "zh-TW" }.Select(l => TurnIntentHeadTestFactory.Verdict(l, true)).ToList();

        TurnIntentLocaleGate.ResolveLocale(language, locales).ShouldBeNull();
    }

    private static TurnIntentLocaleVerdict Verdict(int pairs, int pairsCorrect, int detectorPairs, int askPredicted, int askCorrect) =>
        new("de", true, 100, pairs, pairsCorrect, detectorPairs, askPredicted, askCorrect, 40, 0, 0, 0, 0, 0);
}
