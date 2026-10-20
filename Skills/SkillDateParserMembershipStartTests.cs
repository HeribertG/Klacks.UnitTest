// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for SkillDateParser.ParseMembershipStart: the 'entry' keyword (any case, padded) selects the entry-date mode
/// without a date, every other value is read exactly like ParseOptionalUtcDate in Fixed mode (blank stays open, a date or
/// a relative day word resolves to UTC midnight, garbage is invalid and never silently becomes today).
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SkillDateParserMembershipStartTests
{
    private static readonly DateTime Today = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    [TestCase("entry")]
    [TestCase("ENTRY")]
    [TestCase("  Entry ")]
    public void EntryKeyword_SelectsEntryDateMode_WithoutADate(string raw)
    {
        var (value, mode, invalid) = SkillDateParser.ParseMembershipStart(raw, Today);

        value.ShouldBeNull();
        mode.ShouldBe(GroupMembershipStartMode.EntryDate);
        invalid.ShouldBeFalse();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Blank_IsFixedModeWithoutADate(string? raw)
    {
        var (value, mode, invalid) = SkillDateParser.ParseMembershipStart(raw, Today);

        value.ShouldBeNull();
        mode.ShouldBe(GroupMembershipStartMode.Fixed);
        invalid.ShouldBeFalse();
    }

    [Test]
    public void IsoDate_IsFixedModeAtUtcMidnight()
    {
        var (value, mode, invalid) = SkillDateParser.ParseMembershipStart("2026-05-01", Today);

        value.ShouldBe(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        value!.Value.Kind.ShouldBe(DateTimeKind.Utc);
        mode.ShouldBe(GroupMembershipStartMode.Fixed);
        invalid.ShouldBeFalse();
    }

    [Test]
    public void Today_ResolvesAgainstTheCompanyDay()
    {
        var (value, mode, invalid) = SkillDateParser.ParseMembershipStart("today", Today);

        value.ShouldBe(Today);
        mode.ShouldBe(GroupMembershipStartMode.Fixed);
        invalid.ShouldBeFalse();
    }

    [Test]
    public void Garbage_IsInvalid_AndNeverFallsBackToToday()
    {
        var (value, mode, invalid) = SkillDateParser.ParseMembershipStart("someday soon", Today);

        value.ShouldBeNull();
        mode.ShouldBe(GroupMembershipStartMode.Fixed);
        invalid.ShouldBeTrue();
    }

    [Test]
    public void MissingStartDateMessage_OffersTheEntryKeyword()
    {
        SkillDateParser.MissingStartDateMessage("add the client").ShouldContain("'entry'");
        SkillDateParser.AskForStartDateInPreview.ShouldContain("'entry'");
    }
}
