// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SkillDateParser: blank input defaults (no value, not invalid), ISO and Swiss
/// dotted dates parse to UTC midnight, "today" words resolve to today, and a non-blank value that
/// cannot be understood is flagged Invalid so the skill rejects it instead of silently using now.
/// </summary>

using Klacks.Api.Application.Skills;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SkillDateParserTests
{
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Blank_ReturnsNoValue_AndNotInvalid(string? raw)
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate(raw);

        Assert.That(value, Is.Null);
        Assert.That(invalid, Is.False);
    }

    [Test]
    public void IsoDate_ParsesToUtcMidnight()
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate("2026-05-01");

        Assert.That(invalid, Is.False);
        Assert.That(value, Is.EqualTo(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.That(value!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public void SwissDottedDate_IsReadAsDayMonthYear()
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate("01.05.2026");

        Assert.That(invalid, Is.False);
        Assert.That(value, Is.EqualTo(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [TestCase("today")]
    [TestCase("heute")]
    [TestCase("Heute")]
    [TestCase("ab sofort")]
    public void TodayWords_ResolveToToday(string raw)
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate(raw);

        Assert.That(invalid, Is.False);
        Assert.That(value, Is.EqualTo(DateTime.UtcNow.Date));
    }

    [TestCase("irgendwann bald")]
    [TestCase("nächsten Monat")]
    [TestCase("whenever")]
    public void UnparseableNonBlank_IsFlaggedInvalid(string raw)
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate(raw);

        Assert.That(value, Is.Null);
        Assert.That(invalid, Is.True);
    }
}
