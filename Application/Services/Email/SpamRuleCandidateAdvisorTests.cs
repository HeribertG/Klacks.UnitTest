// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins every branch of the spam rule candidate advisor: no junk folder, an empty folder, too few mails, no recurring
/// domain, recurring domains without a rule (ordered, with their inbox mails), domains an active sender-domain or
/// sender-contains rule already catches (never proposed, counted), the subdomain trap (a rule for the parent domain does
/// not catch a subdomain), a switched-off rule, the own mailbox domain and known contacts (withheld), the listing and read
/// limits, unusable senders and the honest "no recommendation" wording. The result type holds no subject, body or display name.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Email;
using Klacks.Api.Application.Services.Email;
using Klacks.Api.Domain.Models.Email;

namespace Klacks.UnitTest.Application.Services.Email;

[TestFixture]
public class SpamRuleCandidateAdvisorTests
{
    private const int Minimum = SpamRuleCandidateLimits.MinimumJunkMailsPerDomain;
    private const int FewMails = 2;
    private const int ManyMails = 9;
    private const int SomeMails = 4;
    private const int ListingExcess = 5;

    private static SpamRule Rule(SpamRuleType type, string pattern, bool active = true) =>
        new() { RuleType = type, Pattern = pattern, IsActive = active };

    private static SpamRuleCandidateFacts Facts(
        SenderMailCount[] senders,
        SpamRule[]? rules = null,
        string[]? own = null,
        string[]? contacts = null,
        Dictionary<string, int>? inbox = null,
        bool configured = true,
        bool truncated = false,
        int? junkTotal = null)
    {
        var grouping = SpamSenderDomainGrouping.Group(senders);
        return new SpamRuleCandidateFacts(
            configured,
            junkTotal ?? senders.Sum(sender => sender.MailCount),
            truncated,
            grouping,
            rules ?? [],
            (own ?? []).ToHashSet(StringComparer.Ordinal),
            (contacts ?? []).ToHashSet(StringComparer.Ordinal),
            inbox ?? new Dictionary<string, int>());
    }

    private static SenderMailCount Sender(string address, int count) => new(address, count);

    private static IEnumerable<SpamRuleCandidateFindingCode> Codes(SpamRuleCandidateEvaluation evaluation) =>
        evaluation.Findings.Select(finding => finding.Code);

    [Test]
    public void NoJunkFolder_GivesNoRecommendation()
    {
        var evaluation = SpamRuleCandidateAdvisor.Evaluate(Facts([], configured: false));

        Codes(evaluation).ShouldBe([SpamRuleCandidateFindingCode.NoJunkFolder]);
        evaluation.Candidates.ShouldBeEmpty();
        evaluation.Recommendation.ShouldStartWith("No recommendation");
    }

    [Test]
    public void EmptyJunkFolder_GivesNoRecommendation()
    {
        var evaluation = SpamRuleCandidateAdvisor.Evaluate(Facts([]));

        Codes(evaluation).ShouldBe([SpamRuleCandidateFindingCode.JunkFolderEmpty]);
        evaluation.Recommendation.ShouldStartWith("No recommendation");
    }

    [Test]
    public void FewerJunkMailsThanTheMinimum_IsTooLittleData_AndNamesTheNumbers()
    {
        var evaluation = SpamRuleCandidateAdvisor.Evaluate(Facts([Sender("a@spam.example", FewMails)]));

        Codes(evaluation).ShouldBe([SpamRuleCandidateFindingCode.TooFewJunkMails]);
        evaluation.Candidates.ShouldBeEmpty();
        evaluation.MinimumJunkMails.ShouldBe(Minimum);
        evaluation.Recommendation.ShouldStartWith("No recommendation");
        evaluation.Recommendation.ShouldContain($"{FewMails} mail(s)");
    }

    [Test]
    public void EnoughMailsButNoDomainReachesTheMinimum_IsNoRecurringDomain()
    {
        var evaluation = SpamRuleCandidateAdvisor.Evaluate(Facts(
        [
            Sender("a@one.example", FewMails),
            Sender("a@two.example", FewMails)
        ]));

        evaluation.DomainsWithEnoughMails.ShouldBe(0);
        evaluation.Candidates.ShouldBeEmpty();
        Codes(evaluation).ShouldContain(SpamRuleCandidateFindingCode.NoRecurringDomain);
        Codes(evaluation).ShouldContain(SpamRuleCandidateFindingCode.BelowMinimumLeftOut);
        evaluation.Recommendation.ShouldStartWith("No recommendation");
    }

    [Test]
    public void RecurringDomainsWithoutARule_AreListedMostFrequentFirst_WithTheirFacts()
    {
        var evaluation = SpamRuleCandidateAdvisor.Evaluate(Facts(
        [
            Sender("a@small.example", Minimum),
            Sender("a@big.example", ManyMails),
            Sender("b@big.example", SomeMails),
            Sender("a@once.example", 1)
        ],
        inbox: new Dictionary<string, int> { ["small.example"] = 2 }));

        evaluation.CandidateCount.ShouldBe(2);
        evaluation.Candidates.Select(candidate => candidate.Domain).ShouldBe(["big.example", "small.example"]);
        evaluation.Candidates[0].JunkMailCount.ShouldBe(ManyMails + SomeMails);
        evaluation.Candidates[0].SenderCount.ShouldBe(2);
        evaluation.Candidates[0].InboxMailCount.ShouldBe(0);
        evaluation.Candidates[1].InboxMailCount.ShouldBe(2);
        Codes(evaluation).ShouldContain(SpamRuleCandidateFindingCode.DomainAlsoInInbox);
        Codes(evaluation).ShouldContain(SpamRuleCandidateFindingCode.BelowMinimumLeftOut);
        Codes(evaluation).ShouldContain(SpamRuleCandidateFindingCode.OtherRuleTypesNotEvaluated);
        evaluation.Recommendation.ShouldContain("big.example");
        evaluation.Recommendation.ShouldContain($"{ManyMails + SomeMails} junk mails");
        evaluation.Recommendation.ShouldContain("Nothing has been changed");
        evaluation.Recommendation.ShouldContain("not its subdomains");
    }

    [Test]
    public void ADomainAnActiveSenderDomainRuleCatches_IsNeverProposed_ButCounted()
    {
        var evaluation = SpamRuleCandidateAdvisor.Evaluate(Facts(
            [Sender("a@Spam.example", ManyMails), Sender("a@other.example", ManyMails)],
            rules: [Rule(SpamRuleType.SenderDomain, "SPAM.example")]));

        evaluation.Candidates.Select(candidate => candidate.Domain).ShouldBe(["other.example"]);
        evaluation.CoveredDomainCount.ShouldBe(1);
        Codes(evaluation).ShouldContain(SpamRuleCandidateFindingCode.CoveredDomainsLeftOut);
    }

    [Test]
    public void ADomainACoveringSenderContainsRuleCatches_IsNeverProposed()
    {
        var evaluation = SpamRuleCandidateAdvisor.Evaluate(Facts(
            [Sender("a@mail.spam.example", ManyMails), Sender("b@mail.spam.example", ManyMails)],
            rules: [Rule(SpamRuleType.SenderContains, "spam.example")]));

        evaluation.Candidates.ShouldBeEmpty();
        evaluation.CoveredDomainCount.ShouldBe(1);
        evaluation.Recommendation.ShouldStartWith("No recommendation");
        evaluation.Recommendation.ShouldContain("already covered by a rule");
    }

    [Test]
    public void ASenderContainsRuleThatCatchesOnlyOneSenderOfTheDomain_DoesNotCoverTheDomain()
    {
        var evaluation = SpamRuleCandidateAdvisor.Evaluate(Facts(
            [Sender("promo@shop.example", ManyMails), Sender("news@shop.example", ManyMails)],
            rules: [Rule(SpamRuleType.SenderContains, "promo")]));

        evaluation.Candidates.Select(candidate => candidate.Domain).ShouldBe(["shop.example"]);
        evaluation.CoveredDomainCount.ShouldBe(0);
    }

    [Test]
    public void ASubdomainIsNotCaughtByTheParentDomainRule_AndIsProposedWithAWarning()
    {
        var evaluation = SpamRuleCandidateAdvisor.Evaluate(Facts(
            [Sender("a@mail.example.com", ManyMails)],
            rules: [Rule(SpamRuleType.SenderDomain, "example.com")]));

        var candidate = evaluation.Candidates.ShouldHaveSingleItem();
        candidate.Domain.ShouldBe("mail.example.com");
        candidate.HasParentDomainRule.ShouldBeTrue();
        evaluation.CoveredDomainCount.ShouldBe(0);
        Codes(evaluation).ShouldContain(SpamRuleCandidateFindingCode.ParentDomainRuleDoesNotCoverSubdomain);
    }

    [Test]
    public void ANeighbourDomainWithTheSameSuffixTextIsNoParent()
    {
        var evaluation = SpamRuleCandidateAdvisor.Evaluate(Facts(
            [Sender("a@badexample.com", ManyMails)],
            rules: [Rule(SpamRuleType.SenderDomain, "example.com")]));

        evaluation.Candidates.ShouldHaveSingleItem().HasParentDomainRule.ShouldBeFalse();
        Codes(evaluation).ShouldNotContain(SpamRuleCandidateFindingCode.ParentDomainRuleDoesNotCoverSubdomain);
    }

    [Test]
    public void ASwitchedOffRule_MakesTheDomainWithheld_NotACandidate()
    {
        var evaluation = SpamRuleCandidateAdvisor.Evaluate(Facts(
            [Sender("a@spam.example", ManyMails)],
            rules: [Rule(SpamRuleType.SenderDomain, "spam.example", active: false)]));

        evaluation.Candidates.ShouldBeEmpty();
        evaluation.CoveredDomainCount.ShouldBe(0);
        var withheld = evaluation.Withheld.ShouldHaveSingleItem();
        withheld.Reason.ShouldBe(SpamRuleWithheldReason.InactiveRuleExists);
        Codes(evaluation).ShouldContain(SpamRuleCandidateFindingCode.InactiveRuleExists);
    }

    [Test]
    public void AnActiveRuleWins_OverASwitchedOffDuplicate()
    {
        var evaluation = SpamRuleCandidateAdvisor.Evaluate(Facts(
            [Sender("a@spam.example", ManyMails)],
            rules:
            [
                Rule(SpamRuleType.SenderDomain, "spam.example", active: false),
                Rule(SpamRuleType.SenderDomain, "spam.example")
            ]));

        evaluation.CoveredDomainCount.ShouldBe(1);
        evaluation.Withheld.ShouldBeEmpty();
    }

    [Test]
    public void TheOwnMailboxDomain_AndKnownContactDomains_AreWithheldNotProposed()
    {
        var evaluation = SpamRuleCandidateAdvisor.Evaluate(Facts(
            [
                Sender("a@firma.example", ManyMails),
                Sender("a@kunde.example", ManyMails),
                Sender("a@spam.example", ManyMails)
            ],
            own: ["firma.example"],
            contacts: ["kunde.example"]));

        evaluation.Candidates.Select(candidate => candidate.Domain).ShouldBe(["spam.example"]);
        evaluation.Withheld.Select(entry => (entry.Domain, entry.Reason)).ShouldBe(
        [
            ("firma.example", SpamRuleWithheldReason.OwnDomain),
            ("kunde.example", SpamRuleWithheldReason.KnownContactDomain)
        ]);
        Codes(evaluation).ShouldContain(SpamRuleCandidateFindingCode.OwnDomainWithheld);
        Codes(evaluation).ShouldContain(SpamRuleCandidateFindingCode.KnownContactDomainWithheld);
    }

    [Test]
    public void OnlyWithheldDomains_GiveNoRecommendation()
    {
        var evaluation = SpamRuleCandidateAdvisor.Evaluate(Facts(
            [Sender("a@kunde.example", ManyMails)],
            contacts: ["kunde.example"]));

        evaluation.CandidateCount.ShouldBe(0);
        evaluation.Recommendation.ShouldStartWith("No recommendation");
        evaluation.Recommendation.ShouldContain("withheld (1)");
    }

    [Test]
    public void MoreCandidatesThanTheListingLimit_AreCutOrderedAndReportedAsCut()
    {
        var senders = Enumerable.Range(0, SpamRuleCandidateLimits.MaxCandidatesListed + ListingExcess)
            .Select(index => Sender($"a@d{index:D3}.example", Minimum + index))
            .ToArray();

        var evaluation = SpamRuleCandidateAdvisor.Evaluate(Facts(senders));

        evaluation.CandidateCount.ShouldBe(SpamRuleCandidateLimits.MaxCandidatesListed + ListingExcess);
        evaluation.Candidates.Count.ShouldBe(SpamRuleCandidateLimits.MaxCandidatesListed);
        evaluation.MaxCandidatesListed.ShouldBe(SpamRuleCandidateLimits.MaxCandidatesListed);
        evaluation.Candidates.Select(candidate => candidate.JunkMailCount).ShouldBe(
            evaluation.Candidates.Select(candidate => candidate.JunkMailCount).OrderDescending());
        Codes(evaluation).ShouldContain(SpamRuleCandidateFindingCode.CandidateListTruncated);
    }

    [Test]
    public void MoreWithheldDomainsThanTheListingLimit_AreCutButCounted()
    {
        var senders = Enumerable.Range(0, SpamRuleCandidateLimits.MaxWithheldListed + ListingExcess)
            .Select(index => Sender($"a@c{index:D3}.example", Minimum))
            .ToArray();
        var contacts = senders.Select(sender => sender.FromAddress[2..]).ToArray();

        var evaluation = SpamRuleCandidateAdvisor.Evaluate(Facts(senders, contacts: contacts));

        evaluation.WithheldDomainCount.ShouldBe(SpamRuleCandidateLimits.MaxWithheldListed + ListingExcess);
        evaluation.Withheld.Count.ShouldBe(SpamRuleCandidateLimits.MaxWithheldListed);
    }

    [Test]
    public void TruncatedSenderList_AndUnusableSenders_AreReportedHonestly()
    {
        var evaluation = SpamRuleCandidateAdvisor.Evaluate(Facts(
            [Sender("a@spam.example", ManyMails), Sender("no-at-sign", SomeMails)],
            truncated: true,
            junkTotal: ManyMails + SomeMails + ManyMails));

        evaluation.UnusableMailCount.ShouldBe(SomeMails);
        evaluation.SenderListTruncated.ShouldBeTrue();
        Codes(evaluation).ShouldContain(SpamRuleCandidateFindingCode.UnusableSenders);
        Codes(evaluation).ShouldContain(SpamRuleCandidateFindingCode.SenderListTruncated);
        evaluation.Recommendation.ShouldContain($"{SpamRuleCandidateLimits.MaxJunkSenderRows} most frequent");
        evaluation.JunkMailCount.ShouldBe(ManyMails + SomeMails + ManyMails);
    }

    [Test]
    public void TheMinimumIsExactlyTheBoundary()
    {
        var below = SpamRuleCandidateAdvisor.Evaluate(Facts([Sender("a@x.example", Minimum - 1), Sender("a@y.example", Minimum)]));
        below.Candidates.Select(candidate => candidate.Domain).ShouldBe(["y.example"]);
    }

    [Test]
    public void TheSameFacts_GiveTheSameAdviceTwice()
    {
        var facts = Facts([Sender("a@spam.example", ManyMails), Sender("a@other.example", SomeMails)]);

        var first = SpamRuleCandidateAdvisor.Evaluate(facts);
        var second = SpamRuleCandidateAdvisor.Evaluate(facts);

        second.Recommendation.ShouldBe(first.Recommendation);
        second.Candidates.ShouldBe(first.Candidates);
        second.Findings.ShouldBe(first.Findings);
    }

    [Test]
    public void TheResultTypes_CarryNoSubjectBodyOrDisplayName()
    {
        var forbidden = new[] { "Subject", "Body", "FromName", "DisplayName", "Text", "Content" };
        var types = new[]
        {
            typeof(SpamRuleCandidateEvaluation), typeof(SpamRuleCandidate), typeof(SpamRuleWithheldDomain),
            typeof(SpamRuleCandidateFinding)
        };

        var names = types.SelectMany(type => type.GetProperties()).Select(property => property.Name).ToList();

        foreach (var word in forbidden)
        {
            names.ShouldNotContain(name => name.Contains(word, StringComparison.OrdinalIgnoreCase));
        }
    }
}
