// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SkillCalendarStringParser, the single deterministic policy for calendar strings an
/// LLM hands to a skill. Pins that ISO 8601 always wins and is read with InvariantCulture (a th-TH
/// culture would otherwise read "2026-03-04" as the Buddhist year 2026 = Gregorian 1483), that the
/// ambiguous culture formats are resolved from the USER'S language rather than the process culture,
/// and that every assertion holds with CurrentCulture set to en-US as well as de-CH.
/// </summary>

using System.Globalization;
using Klacks.Api.Domain.Services.Assistant.Skills;

namespace Klacks.UnitTest.Domain.Services.Assistant.Skills;

[TestFixture]
public class SkillCalendarStringParserTests
{
    private const string SwissProcessCulture = "de-CH";
    private const string AmericanProcessCulture = "en-US";

    private static readonly string[] ProcessCultures = [AmericanProcessCulture, SwissProcessCulture];

    [TestCase(null)]
    [TestCase("de")]
    [TestCase("en")]
    [TestCase("fr")]
    [TestCase("th")]
    [TestCase("ar")]
    [TestCase("he")]
    [TestCase("ja")]
    public void IsoDate_IsReadTheSameWay_InEveryLanguage(string? language)
    {
        InEveryProcessCulture(() =>
        {
            SkillCalendarStringParser.TryParseDateOnly("2026-03-04", language, out var value).ShouldBeTrue();
            value.ShouldBe(new DateOnly(2026, 3, 4));
        });
    }

    [TestCase("2026-03-04T10:00:00Z")]
    [TestCase("2026-03-04T10:00:00")]
    [TestCase("2026-03-04T10:00")]
    [TestCase("2026-03-04T10:00:00+02:00")]
    public void IsoDateTime_KeepsTheWrittenCalendarDayAndWallClock_UnderABuddhistLanguage(string raw)
    {
        InEveryProcessCulture(() =>
        {
            SkillCalendarStringParser.TryParseDateTime(raw, "th", out var value).ShouldBeTrue();
            value.ShouldBe(new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc));
            value.Kind.ShouldBe(DateTimeKind.Utc);
        });
    }

    [TestCase("04.03.2026", "de", 2026, 3, 4)]
    [TestCase("03/04/2026", "en", 2026, 3, 4)]
    [TestCase("03/04/2026", "fr", 2026, 4, 3)]
    [TestCase("03/04/2026", "pt", 2026, 4, 3)]
    [TestCase("12.09.2026", "th", 2026, 9, 12)]
    [TestCase("12.09.2026", "ar", 2026, 9, 12)]
    [TestCase("12.09.2026", "he", 2026, 9, 12)]
    [TestCase("2026/03/04", "ja", 2026, 3, 4)]
    public void CultureFormat_IsResolvedFromTheUserLanguage(
        string raw, string language, int year, int month, int day)
    {
        InEveryProcessCulture(() =>
        {
            SkillCalendarStringParser.TryParseDateOnly(raw, language, out var value).ShouldBeTrue();
            value.ShouldBe(new DateOnly(year, month, day));
        });
    }

    [Test]
    public void ThaiBuddhistYear_IsConvertedToItsGregorianYear()
    {
        InEveryProcessCulture(() =>
        {
            SkillCalendarStringParser.TryParseDateOnly("12.09.2569", "th", out var value).ShouldBeTrue();
            value.ShouldBe(new DateOnly(2026, 9, 12));
        });
    }

    [TestCase("de")]
    [TestCase("he")]
    [TestCase("ar")]
    [TestCase(null)]
    public void ImplausibleYear_IsRejected_InsteadOfBeingStoredAsWritten(string? language)
    {
        InEveryProcessCulture(() =>
            SkillCalendarStringParser.TryParseDateOnly("12.09.2569", language, out _).ShouldBeFalse());
    }

    [TestCase("2026-03-04", "th")]
    [TestCase("04.03.2026", "de")]
    [TestCase("03/04/2026", "en")]
    [TestCase("03/04/2026", "fr")]
    public void DateOnlyAndDateTime_YieldTheSameDay_ForTheSameInputAndLanguage(string raw, string language)
    {
        InEveryProcessCulture(() =>
        {
            SkillCalendarStringParser.TryParseDateOnly(raw, language, out var dateOnly).ShouldBeTrue();
            SkillCalendarStringParser.TryParseDateTime(raw, language, out var dateTime).ShouldBeTrue();
            DateOnly.FromDateTime(dateTime).ShouldBe(dateOnly);
        });
    }

    [TestCase("14:30", 14, 30)]
    [TestCase("14:30:00", 14, 30)]
    [TestCase("08:05", 8, 5)]
    public void IsoTime_IsReadTheSameWay_InEveryProcessCulture(string raw, int hour, int minute)
    {
        InEveryProcessCulture(() =>
        {
            SkillCalendarStringParser.TryParseTimeOnly(raw, "th", out var value).ShouldBeTrue();
            value.ShouldBe(new TimeOnly(hour, minute));
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("garbage")]
    [TestCase("whenever")]
    public void BlankOrUnparsable_ReturnsFalse(string? raw)
    {
        InEveryProcessCulture(() =>
        {
            SkillCalendarStringParser.TryParseDateOnly(raw, "de", out _).ShouldBeFalse();
            SkillCalendarStringParser.TryParseDateTime(raw, "de", out _).ShouldBeFalse();
            SkillCalendarStringParser.TryParseTimeOnly(raw, "de", out _).ShouldBeFalse();
        });
    }

    [Test]
    public void UnknownLanguage_FallsBackToTheDefaultCultureList()
    {
        InEveryProcessCulture(() =>
        {
            SkillCalendarStringParser.TryParseDateOnly("04.03.2026", "xx", out var value).ShouldBeTrue();
            value.ShouldBe(new DateOnly(2026, 3, 4));
        });
    }

    [Test]
    public void RegionalLanguageTag_IsResolvedByItsBaseLanguage()
    {
        InEveryProcessCulture(() =>
        {
            SkillCalendarStringParser.TryParseDateOnly("03/04/2026", "en-GB", out var value).ShouldBeTrue();
            value.ShouldBe(new DateOnly(2026, 3, 4));
        });
    }

    private static void InEveryProcessCulture(Action assertion)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (var cultureName in ProcessCultures)
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
                assertion();
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
