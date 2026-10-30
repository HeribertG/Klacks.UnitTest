// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the one rule matcher shared by the spam filter and the spam rule candidate advice: SenderDomain is an exact,
/// case-insensitive domain match (a rule for example.com does not catch mail.example.com), the sender domain is read after
/// the last at-sign with display-name form, surrounding blanks and a trailing dot removed, a missing at-sign yields no
/// domain, and Unicode and punycode spellings of one domain stay two different strings.
/// </summary>

using Klacks.Api.Domain.Models.Email;
using Klacks.Api.Domain.Services.Email;

namespace Klacks.UnitTest.Domain.Services.Email;

[TestFixture]
public class SpamRuleMatcherTests
{
    private const string Domain = "example.com";

    private static SpamRule Rule(SpamRuleType type, string pattern) =>
        new() { RuleType = type, Pattern = pattern, IsActive = true };

    private static ReceivedEmail Mail(string fromAddress, string? fromName = null, string subject = "", string? bodyText = null) =>
        new() { FromAddress = fromAddress, FromName = fromName, Subject = subject, BodyText = bodyText };

    [TestCase("anna@example.com", "example.com")]
    [TestCase("Anna@Example.COM", "Example.COM")]
    [TestCase("  anna@example.com  ", "example.com")]
    [TestCase("Anna Muster <anna@example.com>", "example.com")]
    [TestCase("\"Muster, Anna\" <anna@Example.com>", "Example.com")]
    [TestCase("anna@example.com.", "example.com")]
    [TestCase("anna@mail.example.com", "mail.example.com")]
    [TestCase("\"a@b\"@example.com", "example.com")]
    [TestCase("anna@bücher.de", "bücher.de")]
    [TestCase("anna@xn--bcher-kva.de", "xn--bcher-kva.de")]
    public void ExtractDomain_ReadsTheDomainAfterTheLastAtSign(string address, string expected)
    {
        SpamRuleMatcher.ExtractDomain(address).ShouldBe(expected);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("no-at-sign")]
    [TestCase("anna@")]
    [TestCase("anna@.")]
    [TestCase("Anna <anna>")]
    public void ExtractDomain_WithoutADomain_ReturnsNull(string? address)
    {
        SpamRuleMatcher.ExtractDomain(address).ShouldBeNull();
    }

    [Test]
    public void SenderDomain_MatchesTheExactDomain_IgnoringCase()
    {
        var rule = Rule(SpamRuleType.SenderDomain, Domain);

        SpamRuleMatcher.Matches(Mail("anna@EXAMPLE.com"), rule).ShouldBeTrue();
        SpamRuleMatcher.Matches(Mail("Anna <anna@example.com>"), rule).ShouldBeTrue();
    }

    [Test]
    public void SenderDomain_DoesNotMatchASubdomainOrAParentDomain()
    {
        SpamRuleMatcher.Matches(Mail("anna@mail.example.com"), Rule(SpamRuleType.SenderDomain, Domain)).ShouldBeFalse();
        SpamRuleMatcher.Matches(Mail("anna@example.com"), Rule(SpamRuleType.SenderDomain, "mail.example.com")).ShouldBeFalse();
        SpamRuleMatcher.Matches(Mail("anna@notexample.com"), Rule(SpamRuleType.SenderDomain, Domain)).ShouldBeFalse();
    }

    [Test]
    public void SenderDomain_UnicodeAndPunycodeSpellings_AreNotTheSameDomain()
    {
        SpamRuleMatcher.Matches(Mail("anna@bücher.de"), Rule(SpamRuleType.SenderDomain, "xn--bcher-kva.de")).ShouldBeFalse();
        SpamRuleMatcher.Matches(Mail("anna@xn--bcher-kva.de"), Rule(SpamRuleType.SenderDomain, "bücher.de")).ShouldBeFalse();
        SpamRuleMatcher.Matches(Mail("anna@Bücher.DE"), Rule(SpamRuleType.SenderDomain, "bücher.de")).ShouldBeTrue();
    }

    [Test]
    public void SenderDomain_WithoutAtSignOrWithEmptyPattern_NeverMatches()
    {
        SpamRuleMatcher.Matches(Mail("example.com"), Rule(SpamRuleType.SenderDomain, Domain)).ShouldBeFalse();
        SpamRuleMatcher.Matches(Mail("anna@example.com"), Rule(SpamRuleType.SenderDomain, string.Empty)).ShouldBeFalse();
    }

    [Test]
    public void SenderContains_LooksInTheAddressAndTheDisplayName()
    {
        SpamRuleMatcher.Matches(Mail("promo@shop.example"), Rule(SpamRuleType.SenderContains, "PROMO")).ShouldBeTrue();
        SpamRuleMatcher.Matches(Mail("x@y.example", fromName: "Big Promo Team"), Rule(SpamRuleType.SenderContains, "promo")).ShouldBeTrue();
        SpamRuleMatcher.Matches(Mail("x@y.example"), Rule(SpamRuleType.SenderContains, "promo")).ShouldBeFalse();
    }

    [Test]
    public void SubjectAndBodyRules_LookOnlyAtTheirOwnField()
    {
        var mail = Mail("a@b.example", subject: "Win a prize", bodyText: "Click here");

        SpamRuleMatcher.Matches(mail, Rule(SpamRuleType.SubjectContains, "PRIZE")).ShouldBeTrue();
        SpamRuleMatcher.Matches(mail, Rule(SpamRuleType.BodyContains, "click")).ShouldBeTrue();
        SpamRuleMatcher.Matches(mail, Rule(SpamRuleType.SubjectContains, "click")).ShouldBeFalse();
        SpamRuleMatcher.Matches(Mail("a@b.example"), Rule(SpamRuleType.BodyContains, "click")).ShouldBeFalse();
    }

    [Test]
    public void UnknownRuleType_NeverMatches()
    {
        SpamRuleMatcher.Matches(Mail("a@example.com"), Rule((SpamRuleType)99, "example.com")).ShouldBeFalse();
    }
}
