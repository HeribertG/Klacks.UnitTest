// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization of EmailPollingBackgroundService.ProcessEmailAsync beyond EmailPollingBackgroundServiceTests: the
/// rendered log lines (spam, already analysed, failure), the caller's token reaching every collaborator, the
/// case-insensitive folder comparison, the parsing of the analysis feature flag, the sender display and body fallback of
/// the inbound source, the client id fallback for orchestrator and period summary, and the state left behind when the
/// IMAP junk move fails.
/// </summary>

using AppSettings = Klacks.Api.Application.Constants.Settings;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Interfaces.Inbound;
using Klacks.Api.Domain.Models.Email;
using Klacks.Api.Domain.Models.Inbound;
using Klacks.Api.Infrastructure.Email;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Email;

[TestFixture]
public class EmailPollingBackgroundServiceCharacterizationTests
{
    private const string InboxFolder = "INBOX";
    private const string JunkFolder = "Junk";
    private const string Sender = "sender@example.com";

    private IImapEmailService _imapEmailService = null!;
    private ISpamFilterService _spamFilterService = null!;
    private IEmailClientAssignmentService _clientAssignmentService = null!;
    private ISettingsRepository _settingsRepository = null!;
    private IInboundIntentAnalysisService _intentAnalysisService = null!;
    private IInboundAnalysisRepository _analysisRepository = null!;
    private IInboundActionOrchestrator _actionOrchestrator = null!;
    private IEmailPeriodLoadService _periodLoadService = null!;
    private IInboundAnalysisNotifier _analysisNotifier = null!;
    private IClarificationCoordinator _clarificationCoordinator = null!;
    private IUnitOfWork _unitOfWork = null!;
    private RecordingLogger<EmailPollingBackgroundService> _logger = null!;
    private ServiceProvider _provider = null!;
    private IServiceScope _scope = null!;
    private EmailPollingBackgroundService _service = null!;
    private Guid _resolvedClientId;

    [SetUp]
    public void SetUp()
    {
        _imapEmailService = Substitute.For<IImapEmailService>();
        _spamFilterService = Substitute.For<ISpamFilterService>();
        _clientAssignmentService = Substitute.For<IEmailClientAssignmentService>();
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _intentAnalysisService = Substitute.For<IInboundIntentAnalysisService>();
        _analysisRepository = Substitute.For<IInboundAnalysisRepository>();
        _actionOrchestrator = Substitute.For<IInboundActionOrchestrator>();
        _periodLoadService = Substitute.For<IEmailPeriodLoadService>();
        _analysisNotifier = Substitute.For<IInboundAnalysisNotifier>();
        _clarificationCoordinator = Substitute.For<IClarificationCoordinator>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _logger = new RecordingLogger<EmailPollingBackgroundService>();
        _resolvedClientId = Guid.NewGuid();

        _spamFilterService.ClassifyAsync(Arg.Any<ReceivedEmail>(), Arg.Any<CancellationToken>())
            .Returns(new SpamFilterResult { IsSpam = false });
        AnalysisFlagIs("true");
        _clientAssignmentService.ResolveClientAsync(Arg.Any<ReceivedEmail>(), Arg.Any<CancellationToken>())
            .Returns((_resolvedClientId, EntityTypeEnum.Employee));
        _clarificationCoordinator.BeforeAnalysisAsync(Arg.Any<ClarificationRequest>(), Arg.Any<CancellationToken>())
            .Returns(ClarificationPreAnalysis.None);
        _clarificationCoordinator.AfterAnalysisAsync(Arg.Any<ClarificationRequest>(), Arg.Any<InboundAnalysis>(), Arg.Any<CancellationToken>())
            .Returns(ClarificationPostAnalysis.Continue);

        var services = new ServiceCollection();
        services.AddSingleton(_imapEmailService);
        services.AddSingleton(_spamFilterService);
        services.AddSingleton(_clientAssignmentService);
        services.AddSingleton(_settingsRepository);
        services.AddSingleton(_intentAnalysisService);
        services.AddSingleton(_analysisRepository);
        services.AddSingleton(_actionOrchestrator);
        services.AddSingleton(_periodLoadService);
        services.AddSingleton(_analysisNotifier);
        services.AddSingleton(_clarificationCoordinator);
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();

        _service = new EmailPollingBackgroundService(Substitute.For<IServiceScopeFactory>(), _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _service.Dispose();
        _scope.Dispose();
        _provider.Dispose();
    }

    [Test]
    public async Task SpamInTheInbox_LogsTheSenderAndReason_AtInformation()
    {
        var email = Email(InboxFolder);
        _spamFilterService.ClassifyAsync(email, Arg.Any<CancellationToken>())
            .Returns(new SpamFilterResult { IsSpam = true, Reason = "blacklisted" });

        await ProcessAsync(email);

        _logger.Entries.Select(e => (e.Level, e.Message)).ShouldBe(new[]
        {
            (LogLevel.Information, $"Email from {Sender} classified as spam: blacklisted"),
        });
        await _unitOfWork.Received(1).CompleteAsync();
        await _settingsRepository.DidNotReceiveWithAnyArgs().GetSetting(default!);
    }

    [Test]
    public async Task FailingJunkMove_LeavesTheFolderOnJunk_ButTheMailUnprocessedAndUncommitted()
    {
        var email = Email(InboxFolder);
        _spamFilterService.ClassifyAsync(email, Arg.Any<CancellationToken>())
            .Returns(new SpamFilterResult { IsSpam = true, Reason = "blacklisted" });
        _imapEmailService.MoveEmailOnImapAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("imap down"));

        await ProcessAsync(email);

        email.Folder.ShouldBe(JunkFolder);
        email.ProcessedAt.ShouldBeNull();
        await _unitOfWork.DidNotReceive().CompleteAsync();
        _logger.Entries.Count.ShouldBe(1);
        _logger.Entries[0].Level.ShouldBe(LogLevel.Warning);
        _logger.Entries[0].Message.ShouldBe($"Email processing failed for email {email.Id} from {Sender}, will retry next cycle");
        _logger.Entries[0].Exception.ShouldBeOfType<InvalidOperationException>();
    }

    [Test]
    public async Task AlreadyAnalysed_LogsTheWarningWithTheMailId()
    {
        var email = Email(InboxFolder);
        _analysisRepository.ExistsBySourceAsync(InboundSourceKind.Email, email.Id, Arg.Any<CancellationToken>())
            .Returns(true);

        await ProcessAsync(email);

        _logger.Entries.Select(e => (e.Level, e.Message)).ShouldBe(new[]
        {
            (LogLevel.Warning,
                $"message {email.Id} already analyzed — skipped; hard-delete the inbound_analyses row to reprocess"),
        });
        await _clientAssignmentService.Received(1).AssignNewEmailAsync(email);
    }

    [Test]
    public async Task RegularRun_WritesNoLogLine()
    {
        AnalysisReturns(Analysis(_resolvedClientId));

        await ProcessAsync(Email(InboxFolder));

        _logger.Entries.ShouldBeEmpty();
        await _analysisNotifier.ReceivedWithAnyArgs(1).NotifyAsync(default!, default!, default, default, default, default);
    }

    [Test]
    public async Task TheCallersToken_ReachesEveryCollaborator()
    {
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        var email = Email(InboxFolder);
        var analysis = Analysis(_resolvedClientId);
        AnalysisReturns(analysis);

        await ProcessAsync(email, token);

        await _spamFilterService.Received(1).ClassifyAsync(email, token);
        await _analysisRepository.Received(1).ExistsBySourceAsync(InboundSourceKind.Email, email.Id, token);
        await _clientAssignmentService.Received(1).ResolveClientAsync(email, token);
        await _clarificationCoordinator.Received(1).BeforeAnalysisAsync(Arg.Any<ClarificationRequest>(), token);
        await _intentAnalysisService.Received(1).AnalyzeAsync(
            _resolvedClientId, EntityTypeEnum.Employee, Arg.Any<InboundSource>(), token);
        await _analysisRepository.Received(1).AddAsync(analysis, token);
        await _clarificationCoordinator.Received(1).AfterAnalysisAsync(Arg.Any<ClarificationRequest>(), analysis, token);
        await _actionOrchestrator.Received(1).ExecuteAsync(_resolvedClientId, Arg.Any<InboundSource>(), analysis, token);
        await _periodLoadService.Received(1).BuildSummaryAsync(
            _resolvedClientId, analysis.FromDate!.Value, analysis.FromDate.Value, token);
        await _analysisNotifier.Received(1).NotifyAsync(
            Arg.Any<InboundSource>(), analysis, Arg.Any<InboundActionOutcome?>(), Arg.Any<string?>(), Arg.Any<string?>(), token);
    }

    [Test]
    public async Task TheSpamMove_GetsTheCallersToken()
    {
        using var cts = new CancellationTokenSource();
        var email = Email(InboxFolder);
        _spamFilterService.ClassifyAsync(email, Arg.Any<CancellationToken>())
            .Returns(new SpamFilterResult { IsSpam = true, Reason = "r" });

        await ProcessAsync(email, cts.Token);

        await _imapEmailService.Received(1).MoveEmailOnImapAsync(email.ImapUid, InboxFolder, JunkFolder, cts.Token);
    }

    [Test]
    public async Task FolderNames_AreComparedCaseInsensitively()
    {
        var inboxEmail = Email("inbox");

        await ProcessAsync(inboxEmail);

        await _spamFilterService.Received(1).ClassifyAsync(inboxEmail, Arg.Any<CancellationToken>());

        var junkEmail = Email("JUNK");

        await ProcessAsync(junkEmail);

        junkEmail.ProcessedAt.ShouldNotBeNull();
        await _spamFilterService.DidNotReceive().ClassifyAsync(junkEmail, Arg.Any<CancellationToken>());
        await _clientAssignmentService.DidNotReceive().ResolveClientAsync(junkEmail, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MailOutsideInboxAndJunk_SkipsTheSpamStep_ButIsAnalysed()
    {
        var email = Email("ClientAssigned");
        AnalysisReturns(Analysis(_resolvedClientId));

        await ProcessAsync(email);

        await _spamFilterService.DidNotReceiveWithAnyArgs().ClassifyAsync(default!, default);
        await _clientAssignmentService.DidNotReceiveWithAnyArgs().AssignNewEmailAsync(default!);
        await _clientAssignmentService.Received(1).ResolveClientAsync(email, Arg.Any<CancellationToken>());
        email.ProcessedAt.ShouldNotBeNull();
    }

    [TestCase("TRUE", true)]
    [TestCase("True", true)]
    [TestCase("yes", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public async Task AnalysisFlag_IsParsedAsBoolean(string? value, bool analysed)
    {
        AnalysisFlagIs(value);
        AnalysisReturns(Analysis(_resolvedClientId));
        var email = Email(InboxFolder);

        await ProcessAsync(email);

        await _clientAssignmentService.Received(analysed ? 1 : 0).ResolveClientAsync(email, Arg.Any<CancellationToken>());
        await _intentAnalysisService.Received(analysed ? 1 : 0).AnalyzeAsync(
            Arg.Any<Guid>(), Arg.Any<EntityTypeEnum>(), Arg.Any<InboundSource>(), Arg.Any<CancellationToken>());
        email.ProcessedAt.ShouldNotBeNull();
        await _unitOfWork.Received(analysed ? 2 : 1).CompleteAsync();
    }

    [Test]
    public async Task MissingAnalysisSetting_DisablesTheAnalysis()
    {
        _settingsRepository.GetSetting(AppSettings.EMAIL_ANALYSIS_ENABLED)
            .Returns((Klacks.Api.Domain.Models.Settings.Settings?)null);
        var email = Email(InboxFolder);

        await ProcessAsync(email);

        await _clientAssignmentService.DidNotReceiveWithAnyArgs().ResolveClientAsync(default!, default);
        email.ProcessedAt.ShouldNotBeNull();
    }

    [Test]
    public async Task InboundSource_ShowsNameAndAddress_AndFallsBackFromTextToHtmlBody()
    {
        var email = Email(InboxFolder);
        email.FromName = "Anna Muster";
        email.Subject = "Krank";
        email.BodyText = null;
        email.BodyHtml = "<p>krank</p>";
        email.ReceivedDate = new DateTime(2026, 7, 1, 8, 30, 0, DateTimeKind.Utc);
        InboundSource? captured = null;
        _intentAnalysisService.AnalyzeAsync(
                Arg.Any<Guid>(), Arg.Any<EntityTypeEnum>(), Arg.Do<InboundSource>(s => captured = s), Arg.Any<CancellationToken>())
            .Returns(Analysis(_resolvedClientId));

        await ProcessAsync(email);

        captured.ShouldNotBeNull();
        captured.ShouldBe(new InboundSource(
            email.Id, InboundSourceKind.Email, "Email", $"Anna Muster ({Sender})", "Krank", "<p>krank</p>",
            new DateTime(2026, 7, 1, 8, 30, 0, DateTimeKind.Utc)));
    }

    [Test]
    public async Task InboundSource_WithoutNameOrBody_UsesTheAddressAndAnEmptyBody()
    {
        var email = Email(InboxFolder);
        email.FromName = " ";
        email.BodyText = null;
        email.BodyHtml = null;
        InboundSource? captured = null;
        _intentAnalysisService.AnalyzeAsync(
                Arg.Any<Guid>(), Arg.Any<EntityTypeEnum>(), Arg.Do<InboundSource>(s => captured = s), Arg.Any<CancellationToken>())
            .Returns(Analysis(_resolvedClientId));

        await ProcessAsync(email);

        captured.ShouldNotBeNull();
        captured.SenderDisplay.ShouldBe(Sender);
        captured.Body.ShouldBe(string.Empty);
    }

    [Test]
    public async Task AnalysisWithoutClientId_FallsBackToTheResolvedClient_ForOrchestratorAndPeriodSummary()
    {
        var analysis = Analysis(clientId: null);
        analysis.UntilDate = new DateOnly(2026, 7, 3);
        AnalysisReturns(analysis);

        await ProcessAsync(Email(InboxFolder));

        await _actionOrchestrator.Received(1).ExecuteAsync(
            _resolvedClientId, Arg.Any<InboundSource>(), analysis, Arg.Any<CancellationToken>());
        await _periodLoadService.Received(1).BuildSummaryAsync(
            _resolvedClientId, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 3), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnalysisWithAnotherClientId_IsUsed_ForOrchestratorAndPeriodSummary()
    {
        var otherClientId = Guid.NewGuid();
        var analysis = Analysis(otherClientId);
        AnalysisReturns(analysis);

        await ProcessAsync(Email(InboxFolder));

        await _intentAnalysisService.Received(1).AnalyzeAsync(
            _resolvedClientId, EntityTypeEnum.Employee, Arg.Any<InboundSource>(), Arg.Any<CancellationToken>());
        await _actionOrchestrator.Received(1).ExecuteAsync(
            otherClientId, Arg.Any<InboundSource>(), analysis, Arg.Any<CancellationToken>());
        await _periodLoadService.Received(1).BuildSummaryAsync(
            otherClientId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnalysisWithoutFromDate_BuildsNoPeriodSummary_AndPassesNullToTheNotifier()
    {
        var analysis = Analysis(_resolvedClientId);
        analysis.FromDate = null;
        AnalysisReturns(analysis);
        var outcome = new InboundActionOutcome(false, "nothing");
        _actionOrchestrator.ExecuteAsync(Arg.Any<Guid>(), Arg.Any<InboundSource>(), analysis, Arg.Any<CancellationToken>())
            .Returns(outcome);

        await ProcessAsync(Email(InboxFolder));

        await _periodLoadService.DidNotReceiveWithAnyArgs().BuildSummaryAsync(default, default, default, default);
        await _analysisNotifier.Received(1).NotifyAsync(
            Arg.Any<InboundSource>(), analysis, outcome, null, null, Arg.Any<CancellationToken>());
    }

    private void AnalysisFlagIs(string? value) =>
        _settingsRepository.GetSetting(AppSettings.EMAIL_ANALYSIS_ENABLED)
            .Returns(new Klacks.Api.Domain.Models.Settings.Settings { Value = value! });

    private void AnalysisReturns(InboundAnalysis analysis) =>
        _intentAnalysisService.AnalyzeAsync(
                Arg.Any<Guid>(), Arg.Any<EntityTypeEnum>(), Arg.Any<InboundSource>(), Arg.Any<CancellationToken>())
            .Returns(analysis);

    private Task ProcessAsync(ReceivedEmail email, CancellationToken cancellationToken = default) =>
        _service.ProcessEmailAsync(_scope, _unitOfWork, email, InboxFolder, JunkFolder, cancellationToken);

    private static ReceivedEmail Email(string folder) => new()
    {
        Id = Guid.NewGuid(),
        ImapUid = 42,
        Folder = folder,
        FromAddress = Sender,
    };

    private static InboundAnalysis Analysis(Guid? clientId) => new()
    {
        ClientId = clientId,
        ClientType = EntityTypeEnum.Employee,
        FromDate = new DateOnly(2026, 7, 1),
    };
}
