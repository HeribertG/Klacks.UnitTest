// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// RFC 4515 escaping of a typed value before it is placed into an LDAP search filter: backslash, asterisk, both
/// parentheses and NUL become their two-digit hex escape, nothing else changes. The backslash is escaped first, so an
/// escape the encoder produced is never escaped a second time, and a typed filter fragment cannot widen the search.
/// </summary>

using Klacks.Api.Infrastructure.Services.Identity;

namespace Klacks.UnitTest.Infrastructure.Services.Identity;

[TestFixture]
public class LdapFilterEncoderTests
{
    [TestCase("anna", "anna")]
    [TestCase("anna.muster@example.com", "anna.muster@example.com")]
    [TestCase("Müller Straße", "Müller Straße")]
    [TestCase("", "")]
    public void Escape_ValueWithoutSpecialCharacters_IsReturnedUnchanged(string value, string expected)
    {
        LdapFilterEncoder.Escape(value).ShouldBe(expected);
    }

    [TestCase("*", "\\2a")]
    [TestCase("(", "\\28")]
    [TestCase(")", "\\29")]
    [TestCase("\\", "\\5c")]
    [TestCase("\0", "\\00")]
    public void Escape_EachSpecialCharacter_BecomesItsHexEscape(string value, string expected)
    {
        LdapFilterEncoder.Escape(value).ShouldBe(expected);
    }

    [Test]
    public void Escape_Backslash_IsEscapedOnceAndNotAgainByTheOtherEscapes()
    {
        LdapFilterEncoder.Escape("a\\b*").ShouldBe("a\\5cb\\2a");
    }

    [Test]
    public void Escape_InjectionAttempt_LeavesNoActiveFilterSyntax()
    {
        var escaped = LdapFilterEncoder.Escape("*)(uid=*))(|(uid=*");

        escaped.ShouldBe("\\2a\\29\\28uid=\\2a\\29\\29\\28|\\28uid=\\2a");
        escaped.ShouldNotContain("*");
        escaped.ShouldNotContain("(");
        escaped.ShouldNotContain(")");
    }

    [Test]
    public void Escape_ValueWithSeveralSpecialCharacters_EscapesEveryOccurrence()
    {
        LdapFilterEncoder.Escape("(((").ShouldBe("\\28\\28\\28");
    }
}
