// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Locale matrix for the claim extractor: number formats across Swiss/German/English/French
/// grouping and decimal conventions, fullwidth and Arabic-Indic digits, ambiguous single-separator
/// double readings, ignore rules (small integers, years, percentages, times), date formats
/// including CJK and month names, and UUID blanking so their digit groups never become numbers.
/// </summary>

using Klacks.Api.Domain.Models.Assistant.Grounding;
using Klacks.Api.Domain.Services.Assistant.Grounding;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant.Grounding;

[TestFixture]
public class AnswerClaimExtractorTests
{
    private static IReadOnlyList<AnswerClaim> Extract(string text, string? language = "de")
        => AnswerClaimExtractor.Extract(text, language);

    private static List<string> NumberReadings(string text, string? language = "de")
        => Extract(text, language)
            .Where(c => c.Kind == AnswerClaimKind.Number)
            .SelectMany(c => c.Readings)
            .ToList();

    [TestCase("Total 1'234.50 CHF", "1234.5")]
    [TestCase("Total 1’234.50 CHF", "1234.5")]
    [TestCase("Summe 1.234,56 Franken", "1234.56")]
    [TestCase("Total 1,234.56 francs", "1234.56")]
    [TestCase("Montant 1 234,56", "1234.56")]
    [TestCase("Wert 1234.5", "1234.5")]
    [TestCase("Gruppen 12'345", "12345")]
    [TestCase("Stunden 173.5", "173.5")]
    public void NumberFormats_YieldSingleNormalizedReading(string text, string expected)
    {
        NumberReadings(text).ShouldBe(new List<string> { expected });
    }

    [Test]
    public void AmbiguousSingleSeparator_YieldsBothReadings()
    {
        var readings = NumberReadings("Der Wert ist 1.234 gross");

        readings.ShouldBe(new List<string> { "1234", "1.234" });
    }

    [Test]
    public void FullwidthAndArabicIndicDigits_AreNormalized()
    {
        NumberReadings("合計は１２３４５です", "ja").ShouldBe(new List<string> { "12345" });
        NumberReadings("القيمة ٣٤٥ فقط", "ar").ShouldBe(new List<string> { "345" });
    }

    [TestCase("nur 42 Einträge")]
    [TestCase("im Jahr 2026")]
    [TestCase("um 08:30 Uhr")]
    [TestCase("das sind 85 %")]
    [TestCase("genau 99.5 %")]
    public void IgnoreRules_SmallIntegersYearsTimesPercentages(string text)
    {
        NumberReadings(text).ShouldBeEmpty();
    }

    [Test]
    public void DecimalBelowHundred_IsNotIgnored()
    {
        NumberReadings("das sind 42.5 Stunden").ShouldBe(new List<string> { "42.5" });
    }

    [TestCase("am 2026-08-05", "de", "2026-08-05")]
    [TestCase("am 05.08.2026", "de", "2026-08-05")]
    [TestCase("2026年8月5日から", "ja", "2026-08-05")]
    [TestCase("am 5. August 2026", "de", "2026-08-05")]
    [TestCase("on August 5, 2026", "en", "2026-08-05")]
    [TestCase("le 5 août 2026", "fr", "2026-08-05")]
    public void DateFormats_YieldIsoReading(string text, string language, string expected)
    {
        var dates = Extract(text, language).Where(c => c.Kind == AnswerClaimKind.Date).ToList();

        dates.ShouldHaveSingleItem();
        dates[0].Readings.ShouldContain(expected);
    }

    [Test]
    public void SlashDate_YieldsBothDayFirstAndMonthFirstReadings()
    {
        var dates = Extract("on 5/8/2026", "en").Where(c => c.Kind == AnswerClaimKind.Date).ToList();

        dates.ShouldHaveSingleItem();
        dates[0].Readings.ShouldBe(new List<string> { "2026-08-05", "2026-05-08" });
    }

    [Test]
    public void DateDigits_DoNotLeakIntoNumberClaims()
    {
        var claims = Extract("Der Dienst am 05.08.2026 dauert 173.5 Stunden", "de");

        claims.Count(c => c.Kind == AnswerClaimKind.Date).ShouldBe(1);
        claims.Where(c => c.Kind == AnswerClaimKind.Number)
            .SelectMany(c => c.Readings)
            .ShouldBe(new[] { "173.5" });
    }

    [Test]
    public void Uuid_IsExtractedLowercase_AndItsDigitsNeverBecomeNumbers()
    {
        var uuid = "3F2A0C1D-9B4E-4F6A-8C2D-1234567890AB";
        var claims = Extract($"Die Schicht {uuid} wurde angepasst.", "de");

        var uuidClaims = claims.Where(c => c.Kind == AnswerClaimKind.Uuid).ToList();
        uuidClaims.ShouldHaveSingleItem();
        uuidClaims[0].Readings.ShouldBe(new[] { uuid.ToLowerInvariant() });
        claims.Where(c => c.Kind == AnswerClaimKind.Number).ShouldBeEmpty();
    }

    [Test]
    public void MixedSentence_ExtractsEveryClaimClassOnce()
    {
        var claims = Extract("Total: 1'234.50 CHF am 05.08.2026 für 3f2a0c1d-9b4e-4f6a-8c2d-1234567890ab.", "de");

        claims.Count(c => c.Kind == AnswerClaimKind.Uuid).ShouldBe(1);
        claims.Count(c => c.Kind == AnswerClaimKind.Date).ShouldBe(1);
        claims.Where(c => c.Kind == AnswerClaimKind.Number)
            .SelectMany(c => c.Readings)
            .ShouldBe(new[] { "1234.5" });
    }

    [Test]
    public void PlainProse_YieldsNoClaims()
    {
        Extract("Dazu habe ich keine Daten, das kann ich nicht nachschlagen.", "de").ShouldBeEmpty();
    }
}
