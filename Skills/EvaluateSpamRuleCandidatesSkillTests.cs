// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for evaluate_spam_rule_candidates: the skill reads the junk folder as grouped sender counts only (never
/// a single mail), looks up the same domains in the inbox and client folders, takes the own mailbox domain from the
/// settings and contact domains from the contact addresses, leaves out senders of clients the caller may not see, relays
/// the advisor's evaluation, says honestly when no junk folder exists and changes nothing.
/// </summary>

using System.Text.Json;
using AppSettings = Klacks.Api.Application.Constants.Settings;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Email;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Models.Email;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class EvaluateSpamRuleCandidatesSkillTests
{
    private const string JunkFolder = "INBOX.Junk";
    private const string InboxFolder = "INBOX";
    private const string ContactDisplayName = "1042, Anna, Beispiel";
    private const int ManyMails = 9;
    private const int InboxMails = 2;

    private IReceivedEmailRepository _receivedEmails = null!;
    private ISpamRuleRepository _rules = null!;
    private IEmailFolderRepository _folders = null!;
    private IEmailQueryRepository _emailQuery = null!;
    private IClientVisibilityGuard _visibility = null!;
    private ISettingsReader _settings = null!;
    private EvaluateSpamRuleCandidatesSkill _skill = null!;
    private CancellationTokenSource _cancellation = null!;

    [SetUp]
    public void SetUp()
    {
        _receivedEmails = Substitute.For<IReceivedEmailRepository>();
        _rules = Substitute.For<ISpamRuleRepository>();
        _folders = Substitute.For<IEmailFolderRepository>();
        _emailQuery = Substitute.For<IEmailQueryRepository>();
        _visibility = Substitute.For<IClientVisibilityGuard>();
        _settings = Substitute.For<ISettingsReader>();
        _cancellation = new CancellationTokenSource();

        _folders.GetImapNameBySpecialUseAsync(FolderSpecialUse.Junk).Returns(JunkFolder);
        _folders.GetImapNameBySpecialUseAsync(FolderSpecialUse.Inbox).Returns(InboxFolder);
        _rules.GetAllAsync().Returns(new List<SpamRule>());
        _emailQuery.GetClientsWithEmailCommunicationsAsync(_cancellation.Token).Returns(new List<ClientEmailInfo>());
        _settings.GetSettingsByTypesAsync(Arg.Any<IEnumerable<string>>(), _cancellation.Token)
            .Returns(new Dictionary<string, string>());
        _receivedEmails.GetMailCountsBySenderDomainAsync(
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), _cancellation.Token)
            .Returns(new List<SenderDomainMailCount>());

        _skill = new EvaluateSpamRuleCandidatesSkill(_receivedEmails, _rules, _folders, _emailQuery, _visibility, _settings);
    }

    [TearDown]
    public void TearDown() => _cancellation.Dispose();

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Roles.Admin, Permissions.CanEditSettings, Permissions.CanEditClients }
    };

    private void JunkSenders(bool truncated, params SenderMailCount[] senders) =>
        _receivedEmails.GetSenderMailCountsAsync(
                JunkFolder, Arg.Any<IReadOnlyCollection<string>?>(), SpamRuleCandidateLimits.MaxJunkSenderRows, _cancellation.Token)
            .Returns(new SenderMailCounts(senders, senders.Sum(sender => sender.MailCount), truncated));

    private Task<SkillResult> Run() =>
        _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>(), _cancellation.Token);

    [Test]
    public async Task RecurringJunkDomain_IsReported_WithItsInboxMails_AndChangesNothing()
    {
        JunkSenders(false, new SenderMailCount("a@spam.example", ManyMails), new SenderMailCount("a@fine.example", 1));
        _receivedEmails.GetMailCountsBySenderDomainAsync(
                Arg.Is<IReadOnlyCollection<string>>(folders =>
                    folders.Contains(InboxFolder) && folders.Contains(EmailConstants.ClientAssignedFolder)),
                Arg.Any<IReadOnlyCollection<string>>(), _cancellation.Token)
            .Returns(new List<SenderDomainMailCount> { new("spam.example", InboxMails) });

        var result = await Run();

        result.Success.ShouldBeTrue();
        var evaluation = result.Data.ShouldBeOfType<SpamRuleCandidateEvaluation>();
        var candidate = evaluation.Candidates.ShouldHaveSingleItem();
        candidate.Domain.ShouldBe("spam.example");
        candidate.JunkMailCount.ShouldBe(ManyMails);
        candidate.InboxMailCount.ShouldBe(InboxMails);
        result.Message.ShouldBe(evaluation.Recommendation);

        await _rules.DidNotReceiveWithAnyArgs().AddAsync(default!);
        await _rules.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
        await _rules.DidNotReceiveWithAnyArgs().DeleteAsync(default);
        await _receivedEmails.DidNotReceiveWithAnyArgs().MoveToFolderAsync(default, default!);
        await _receivedEmails.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
    }

    [Test]
    public async Task OnlyGroupedCounts_AreRead_NeverASingleMail()
    {
        JunkSenders(false, new SenderMailCount("a@spam.example", ManyMails));

        await Run();

        await _receivedEmails.DidNotReceiveWithAnyArgs().GetListAsync(default, default);
        await _receivedEmails.DidNotReceiveWithAnyArgs().GetListByFolderAsync(default!, default, default);
        await _receivedEmails.DidNotReceiveWithAnyArgs().GetFilteredListAsync(default, default, default, default, default);
        await _receivedEmails.DidNotReceiveWithAnyArgs().GetByIdAsync(default);
    }

    [Test]
    public async Task TheInboxLookup_AsksOnlyForTheRecurringDomains_InTheInboxAndClientFolders()
    {
        JunkSenders(false, new SenderMailCount("a@spam.example", ManyMails), new SenderMailCount("a@once.example", 1));

        await Run();

        await _receivedEmails.Received(1).GetMailCountsBySenderDomainAsync(
            Arg.Is<IReadOnlyCollection<string>>(folders =>
                folders.Count == 2 && folders.Contains(InboxFolder) && folders.Contains(EmailConstants.ClientAssignedFolder)),
            Arg.Is<IReadOnlyCollection<string>>(domains => domains.Single() == "spam.example"),
            _cancellation.Token);
    }

    [Test]
    public async Task WithoutAJunkFolder_ItSaysSo_AndReadsNoSenders()
    {
        _folders.GetImapNameBySpecialUseAsync(FolderSpecialUse.Junk).Returns((string?)null);

        var result = await Run();

        result.Success.ShouldBeTrue();
        result.Data.ShouldBeOfType<SpamRuleCandidateEvaluation>().Findings.Single().Code
            .ShouldBe(SpamRuleCandidateFindingCode.NoJunkFolder);
        result.Message.ShouldStartWith("No recommendation");
        await _receivedEmails.DidNotReceiveWithAnyArgs().GetSenderMailCountsAsync(default!, default, default, default);
    }

    [Test]
    public async Task TheOwnMailboxDomain_FromTheSettings_IsWithheld()
    {
        JunkSenders(false, new SenderMailCount("a@firma.example", ManyMails), new SenderMailCount("a@spam.example", ManyMails));
        _settings.GetSettingsByTypesAsync(Arg.Any<IEnumerable<string>>(), _cancellation.Token).Returns(
            new Dictionary<string, string>
            {
                [AppSettings.APP_EMAIL_ADDRESS] = "Planung <Info@Firma.example>",
                [AppSettings.APP_INCOMING_SERVER_USERNAME] = "not-an-address"
            });

        var result = await Run();

        var evaluation = result.Data.ShouldBeOfType<SpamRuleCandidateEvaluation>();
        evaluation.Candidates.Select(candidate => candidate.Domain).ShouldBe(["spam.example"]);
        evaluation.Withheld.Single().Reason.ShouldBe(SpamRuleWithheldReason.OwnDomain);
        await _settings.Received(1).GetSettingsByTypesAsync(
            Arg.Is<IEnumerable<string>>(keys => keys.OrderBy(key => key).SequenceEqual(new[]
            {
                AppSettings.APP_EMAIL_ADDRESS, AppSettings.APP_INCOMING_SERVER_USERNAME, AppSettings.APP_OUTGOING_SERVER_USERNAME
            }.OrderBy(key => key))),
            _cancellation.Token);
    }

    [Test]
    public async Task AKnownContactsDomain_IsWithheld_AndNoContactNameLeavesTheResult()
    {
        JunkSenders(false, new SenderMailCount("a@kunde.example", ManyMails), new SenderMailCount("a@spam.example", ManyMails));
        _emailQuery.GetClientsWithEmailCommunicationsAsync(_cancellation.Token).Returns(new List<ClientEmailInfo>
        {
            new() { ClientId = Guid.NewGuid(), EmailAddress = "Anna@Kunde.example", ClientDisplayName = ContactDisplayName }
        });
        _visibility.FilterVisibleAsync(
                Arg.Any<IReadOnlyCollection<ClientEmailInfo>>(), Arg.Any<Func<ClientEmailInfo, Guid>>(), _cancellation.Token)
            .Returns(call => call.Arg<IReadOnlyCollection<ClientEmailInfo>>().ToList());

        var result = await Run();

        var evaluation = result.Data.ShouldBeOfType<SpamRuleCandidateEvaluation>();
        evaluation.Candidates.Select(candidate => candidate.Domain).ShouldBe(["spam.example"]);
        evaluation.Withheld.Single().Reason.ShouldBe(SpamRuleWithheldReason.KnownContactDomain);
        JsonSerializer.Serialize(evaluation).ShouldNotContain("Anna");
        JsonSerializer.Serialize(evaluation).ShouldNotContain(ContactDisplayName);
        result.Message!.ShouldNotContain("Anna");
    }

    [Test]
    public async Task SendersOfClientsTheCallerMayNotSee_AreLeftOutOfTheJunkCount()
    {
        var visibleId = Guid.NewGuid();
        var hiddenId = Guid.NewGuid();
        JunkSenders(false, new SenderMailCount("a@spam.example", ManyMails));
        _emailQuery.GetClientsWithEmailCommunicationsAsync(_cancellation.Token).Returns(new List<ClientEmailInfo>
        {
            new() { ClientId = visibleId, EmailAddress = "visible@kunde.example" },
            new() { ClientId = hiddenId, EmailAddress = "Hidden@Kunde.example" }
        });
        _visibility.FilterVisibleAsync(
                Arg.Any<IReadOnlyCollection<ClientEmailInfo>>(), Arg.Any<Func<ClientEmailInfo, Guid>>(), _cancellation.Token)
            .Returns(call => call.Arg<IReadOnlyCollection<ClientEmailInfo>>().Where(c => c.ClientId == visibleId).ToList());

        await Run();

        await _receivedEmails.Received(1).GetSenderMailCountsAsync(
            JunkFolder,
            Arg.Is<IReadOnlyCollection<string>?>(excluded => excluded != null && excluded.Single() == "hidden@kunde.example"),
            SpamRuleCandidateLimits.MaxJunkSenderRows,
            _cancellation.Token);
    }

    [Test]
    public async Task ADomainAnExistingRuleCatches_IsNotProposed()
    {
        JunkSenders(false, new SenderMailCount("a@spam.example", ManyMails));
        _rules.GetAllAsync().Returns(new List<SpamRule>
        {
            new() { RuleType = SpamRuleType.SenderDomain, Pattern = "Spam.Example", IsActive = true }
        });

        var result = await Run();

        var evaluation = result.Data.ShouldBeOfType<SpamRuleCandidateEvaluation>();
        evaluation.Candidates.ShouldBeEmpty();
        evaluation.CoveredDomainCount.ShouldBe(1);
    }

    [Test]
    public async Task ASenderListCutByTheReadLimit_IsReportedAsCut()
    {
        JunkSenders(true, new SenderMailCount("a@spam.example", ManyMails));

        var result = await Run();

        result.Data.ShouldBeOfType<SpamRuleCandidateEvaluation>().SenderListTruncated.ShouldBeTrue();
    }
}
