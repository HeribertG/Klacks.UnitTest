// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SkillDateParser: blank input defaults (no value, not invalid), ISO and Swiss
/// dotted dates parse to UTC midnight, "today" words resolve to the supplied company-today (not the
/// server's UTC day), and a non-blank value that cannot be understood is flagged Invalid so the skill
/// rejects it instead of silently using now. Relative day words are resolved in the caller's language
/// plus English; without a language the historical union of all 25 languages is kept, which is what
/// the callers that have no user (batch, scheduler) rely on.
/// </summary>

using Klacks.Api.Application.Skills;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SkillDateParserTests
{
    private static readonly DateTime Today = new(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc);

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Blank_ReturnsNoValue_AndNotInvalid(string? raw)
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate(raw, Today);

        Assert.That(value, Is.Null);
        Assert.That(invalid, Is.False);
    }

    [Test]
    public void IsoDate_ParsesToUtcMidnight()
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate("2026-05-01", Today);

        Assert.That(invalid, Is.False);
        Assert.That(value, Is.EqualTo(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.That(value!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public void SwissDottedDate_IsReadAsDayMonthYear()
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate("01.05.2026", Today);

        Assert.That(invalid, Is.False);
        Assert.That(value, Is.EqualTo(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void UtcDateTimeWithOffset_TakesOnlyTheWrittenCalendarDay_NotShiftedByServerTimeZone()
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate("2026-08-01T00:00:00+02:00", Today);

        Assert.That(invalid, Is.False);
        Assert.That(value, Is.EqualTo(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.That(value!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public void UtcDateTimeWithZ_IsReadAsThatCalendarDay()
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate("2026-08-01T00:00:00Z", Today);

        Assert.That(invalid, Is.False);
        Assert.That(value, Is.EqualTo(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.That(value!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [TestCase("today")]
    [TestCase("heute")]
    [TestCase("Heute")]
    [TestCase("ab sofort")]
    public void TodayWords_ResolveToSuppliedCompanyToday(string raw)
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate(raw, Today);

        Assert.That(invalid, Is.False);
        Assert.That(value, Is.EqualTo(Today));
        Assert.That(value!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [TestCase("irgendwann bald")]
    [TestCase("nächsten Monat")]
    [TestCase("whenever")]
    public void UnparseableNonBlank_IsFlaggedInvalid(string raw)
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate(raw, Today);

        Assert.That(value, Is.Null);
        Assert.That(invalid, Is.True);
    }

    [TestCase("tomorrow")]
    [TestCase("morgen")]
    [TestCase("明日")]
    [TestCase("พรุ่งนี้")]
    [TestCase("mañana")]
    [TestCase("ngày mai")]
    public void TomorrowWords_ResolveToTheCompanyDayPlusOne(string raw)
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate(raw, Today);

        Assert.That(invalid, Is.False);
        Assert.That(value, Is.EqualTo(Today.AddDays(1)));
        Assert.That(value!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [TestCase("yesterday")]
    [TestCase("gestern")]
    [TestCase("昨日")]
    [TestCase("เมื่อวาน")]
    [TestCase("ayer")]
    [TestCase("i går")]
    public void YesterdayWords_ResolveToTheCompanyDayMinusOne(string raw)
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate(raw, Today);

        Assert.That(invalid, Is.False);
        Assert.That(value, Is.EqualTo(Today.AddDays(-1)));
        Assert.That(value!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [TestCase("今日")]
    [TestCase("วันนี้")]
    [TestCase("hoy")]
    [TestCase("heute")]
    public void TodayWords_InAnySupportedLanguage_ResolveToTheCompanyDay(string raw)
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate(raw, Today);

        Assert.That(invalid, Is.False);
        Assert.That(value, Is.EqualTo(Today));
    }

    [TestCase("hier", "fr", -1)]
    [TestCase("tomorrow", "ja", 1)]
    [TestCase("\u660e\u65e5", "ja", 1)]
    [TestCase("morgen", "de", 1)]
    [TestCase("morgen", "nl", 1)]
    [TestCase("heute", "de-CH", 0)]
    [TestCase("\u4eca\u5929", "zh-CN", 0)]
    [TestCase("yesterday", "th", -1)]
    public void RelativeDayWord_OfTheUsersLanguageOrEnglish_Resolves(
        string raw, string language, int expectedOffset)
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate(raw, Today, language);

        Assert.That(invalid, Is.False);
        Assert.That(value, Is.EqualTo(Today.AddDays(expectedOffset)));
        Assert.That(value!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [TestCase("hier", "de")]
    [TestCase("\u660e\u65e5", "de")]
    [TestCase("gestern", "fr")]
    [TestCase("ma\u00f1ana", "de")]
    public void RelativeDayWord_OfAnotherLanguage_IsFlaggedInvalid(string raw, string language)
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate(raw, Today, language);

        Assert.That(value, Is.Null);
        Assert.That(invalid, Is.True);
    }

    [TestCase("hier", -1)]
    [TestCase("\u660e\u65e5", 1)]
    [TestCase("\u0e27\u0e31\u0e19\u0e19\u0e35\u0e49", 0)]
    public void WithoutALanguage_EveryLanguagesWordsStillResolve(string raw, int expectedOffset)
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate(raw, Today);

        Assert.That(invalid, Is.False);
        Assert.That(value, Is.EqualTo(Today.AddDays(expectedOffset)));
    }

    [Test]
    public void AmbiguousSlashDate_IsReadWithTheUsersLanguage()
    {
        var (english, _) = SkillDateParser.ParseOptionalUtcDate("03/04/2026", Today, "en");
        var (french, _) = SkillDateParser.ParseOptionalUtcDate("03/04/2026", Today, "fr");

        Assert.That(english, Is.EqualTo(new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc)));
        Assert.That(french, Is.EqualTo(new DateTime(2026, 4, 3, 0, 0, 0, DateTimeKind.Utc)));
    }
}
