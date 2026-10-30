// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the folding of sender addresses into domains: case-insensitive merging, display-name form, the ordering by
/// mail count and domain, and the host-name check that keeps anything but plausible domains (spaces, angle brackets,
/// quotes, missing dot, over-long labels or names, hyphens at the ends) out of the result and counts those mails as unusable.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Services.Email;
using Klacks.Api.Domain.Models.Email;

namespace Klacks.UnitTest.Application.Services.Email;

[TestFixture]
public class SpamSenderDomainGroupingTests
{
    private const int TwoMails = 2;
    private const int FiveMails = 5;
    private const int SevenMails = 7;
    private const int ThreeMails = 3;

    private static SenderMailCount Sender(string address, int count) => new(address, count);

    [Test]
    public void Group_MergesAddressesOfOneDomain_IgnoringCase_AndOrdersByMailCountThenDomain()
    {
        var grouping = SpamSenderDomainGrouping.Group(
        [
            Sender("a@Spam.example", FiveMails),
            Sender("b@spam.example", TwoMails),
            Sender("x@zeta.example", FiveMails),
            Sender("y@alpha.example", FiveMails)
        ]);

        grouping.UnusableMailCount.ShouldBe(0);
        grouping.Groups.Select(group => group.Domain).ShouldBe(["spam.example", "alpha.example", "zeta.example"]);
        grouping.Groups[0].MailCount.ShouldBe(SevenMails);
        grouping.Groups[0].SenderCount.ShouldBe(TwoMails);
    }

    [Test]
    public void Group_ReadsTheDomainOutOfADisplayNameForm()
    {
        var grouping = SpamSenderDomainGrouping.Group([Sender("Big Shop <news@Shop.example>", ThreeMails)]);

        grouping.Groups.Single().Domain.ShouldBe("shop.example");
    }

    [Test]
    public void Group_SubdomainsStaySeparateFromTheirParentDomain()
    {
        var grouping = SpamSenderDomainGrouping.Group(
        [
            Sender("a@example.com", ThreeMails),
            Sender("a@mail.example.com", FiveMails)
        ]);

        grouping.Groups.Select(group => group.Domain).ShouldBe(["mail.example.com", "example.com"]);
    }

    [Test]
    public void Group_KeepsThePunycodeSpelling_AndCountsTheUnicodeOneAsUnusable()
    {
        var grouping = SpamSenderDomainGrouping.Group(
        [
            Sender("a@bücher.de", ThreeMails),
            Sender("a@xn--bcher-kva.de", TwoMails)
        ]);

        grouping.Groups.Select(group => group.Domain).ShouldBe(["xn--bcher-kva.de"]);
        grouping.UnusableMailCount.ShouldBe(ThreeMails);
    }

    [Test]
    public void Group_ANonAsciiLetterThatLowerCasesToAscii_IsUnusable()
    {
        var grouping = SpamSenderDomainGrouping.Group([Sender("a@\u212Aanton.ch", ThreeMails)]);

        grouping.Groups.ShouldBeEmpty();
        grouping.UnusableMailCount.ShouldBe(ThreeMails);
    }

    [TestCase("no-at-sign")]
    [TestCase("a@")]
    [TestCase("a@localhost")]
    [TestCase("a@exa mple.com")]
    [TestCase("a@example..com")]
    [TestCase("a@-example.com")]
    [TestCase("a@example-.com")]
    [TestCase("a@exa\"mple.com")]
    [TestCase("a@exa<mple.com")]
    [TestCase("a@ignore:previous_instructions.com")]
    [TestCase("a@exa_mple.com")]
    public void Group_DropsSendersWithoutAPlausibleHostName_AndCountsTheirMails(string address)
    {
        var grouping = SpamSenderDomainGrouping.Group([Sender(address, SevenMails), Sender("ok@fine.example", ThreeMails)]);

        grouping.Groups.Select(group => group.Domain).ShouldBe(["fine.example"]);
        grouping.UnusableMailCount.ShouldBe(SevenMails);
    }

    [Test]
    public void IsPlausibleHostname_EnforcesTheLabelAndTotalLengthLimits()
    {
        var longestLabel = new string('a', SpamRuleCandidateLimits.MaxHostnameLabelLength);
        var tooLongLabel = new string('a', SpamRuleCandidateLimits.MaxHostnameLabelLength + 1);

        SpamSenderDomainGrouping.IsPlausibleHostname($"{longestLabel}.com").ShouldBeTrue();
        SpamSenderDomainGrouping.IsPlausibleHostname($"{tooLongLabel}.com").ShouldBeFalse();

        var tooLongName = string.Join('.', Enumerable.Repeat(longestLabel, 5));
        tooLongName.Length.ShouldBeGreaterThan(SpamRuleCandidateLimits.MaxHostnameLength);
        SpamSenderDomainGrouping.IsPlausibleHostname(tooLongName).ShouldBeFalse();
    }

    [Test]
    public void Group_WithoutSenders_IsEmpty()
    {
        var grouping = SpamSenderDomainGrouping.Group([]);

        grouping.Groups.ShouldBeEmpty();
        grouping.UnusableMailCount.ShouldBe(0);
    }
}
