// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for EmailPollingBackgroundService — verifies the per-email orchestration
/// (ProcessEmailAsync: spam-classify -> junk move OR client assignment -> feature-gate ->
/// sender resolution -> intent analysis -> persist -> action orchestration -> notify) and the
/// batched reclassification pagination (ClassifyFolderBatchedAsync).
/// </summary>

using AppSettings = Klacks.Api.Application.Constants.Settings;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Interfaces.Inbound;
using Klacks.Api.Domain.Models.Email;
using Klacks.Api.Domain.Models.Inbound;
using Klacks.Api.Infrastructure.Email;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Email;

[TestFixture]
public class EmailPollingBackgroundServiceTests
{
    private const string InboxFolder = "INBOX";
    private const string JunkFolder = "Junk";

    private IImapEmailService _imapEmailService = null!;
    private ISpamFilterService _spamFilterService = null!;
    private IEmailClientAssignmentService _clientAssignmentService = null!;
    private ISettingsRepository _settingsRepository = null!;
    private IInboundIntentAnalysisService _intentAnalysisService = null!;
    private IInboundAnalysisRepository _analysisRepository = null!;
    private IInboundActionOrchestrator _actionOrchestrator = null!;
    private IEmailPeriodLoadService _periodLoadService = null!;
    private IInboundAnalysisNotifier _analysisNotifier = null!;
    private IReceivedEmailRepository _receivedEmailRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ServiceProvider _provider = null!;
    private IServiceScope _scope = null!;
    private EmailPollingBackgroundService _service = null!;

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
        _receivedEmailRepository = Substitute.For<IReceivedEmailRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _spamFilterService.ClassifyAsync(Arg.Any<ReceivedEmail>(), Arg.Any<CancellationToken>())
            .Returns(new SpamFilterResult { IsSpam = false });
        _settingsRepository.GetSetting(AppSettings.EMAIL_ANALYSIS_ENABLED)
            .Returns(new Klacks.Api.Domain.Models.Settings.Settings { Value = "true" });
        _clientAssignmentService.ResolveClientAsync(Arg.Any<ReceivedEmail>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), EntityTypeEnum.Employee));

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
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();

        _service = new EmailPollingBackgroundService(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ILogger<EmailPollingBackgroundService>>());
    }

    [TearDown]
    public void TearDown()
    {
        _service.Dispose();
        _scope.Dispose();
        _provider.Dispose();
    }

    private static ReceivedEmail Email(string folder) => new()
    {
        Id = Guid.NewGuid(),
        ImapUid = 42,
        Folder = folder,
        FromAddress = "sender@example.com",
    };

    private static InboundAnalysis Analysis(
        EntityTypeEnum clientType = EntityTypeEnum.Employee,
        Guid? clientId = null,
        DateOnly? fromDate = null,
        DateOnly? untilDate = null) => new()
    {
        ClientId = clientId ?? Guid.NewGuid(),
        ClientType = clientType,
        FromDate = fromDate ?? new DateOnly(2026, 7, 1),
        UntilDate = untilDate,
    };

    private void AnalysisServiceReturns(InboundAnalysis analysis) =>
        _intentAnalysisService.AnalyzeAsync(
            Arg.Any<Guid>(), Arg.Any<EntityTypeEnum>(), Arg.Any<InboundSource>(), Arg.Any<CancellationToken>())
            .Returns(analysis);

    private Task ProcessAsync(ReceivedEmail email, CancellationToken cancellationToken = default) =>
        _service.ProcessEmailAsync(_scope, _unitOfWork, email, InboxFolder, JunkFolder, cancellationToken);

    [Test]
    public async Task InboxEmail_ClassifiedAsSpam_MovesToJunkAndSkipsClientAssignmentAndAnalysis()
    {
        var email = Email(InboxFolder);
        _spamFilterService.ClassifyAsync(email, Arg.Any<CancellationToken>())
            .Returns(new SpamFilterResult { IsSpam = true, Reason = "blacklisted" });

        await ProcessAsync(email);

        email.Folder.ShouldBe(JunkFolder);
        await _imapEmailService.Received(1).MoveEmailOnImapAsync(
            email.ImapUid, InboxFolder, JunkFolder, Arg.Any<CancellationToken>());
        await _clientAssignmentService.DidNotReceive().AssignNewEmailAsync(Arg.Any<ReceivedEmail>());
        await _intentAnalysisService.DidNotReceive().AnalyzeAsync(
            Arg.Any<Guid>(), Arg.Any<EntityTypeEnum>(), Arg.Any<InboundSource>(), Arg.Any<CancellationToken>());
        email.ProcessedAt.ShouldNotBeNull();
    }

    [Test]
    public async Task InboxEmail_NotSpam_AssignsToClient_DoesNotMoveOnImap()
    {
        var email = Email(InboxFolder);

        await ProcessAsync(email);

        await _clientAssignmentService.Received(1).AssignNewEmailAsync(email);
        await _imapEmailService.DidNotReceiveWithAnyArgs().MoveEmailOnImapAsync(default, default!, default!, default);
    }

    [Test]
    public async Task EmailAlreadyInJunkFolder_MarksProcessed_ReturnsWithoutAnalysis()
    {
        var email = Email(JunkFolder);

        await ProcessAsync(email);

        email.ProcessedAt.ShouldNotBeNull();
        await _unitOfWork.Received(1).CompleteAsync();
        await _spamFilterService.DidNotReceiveWithAnyArgs().ClassifyAsync(default!, default);
        await _intentAnalysisService.DidNotReceive().AnalyzeAsync(
            Arg.Any<Guid>(), Arg.Any<EntityTypeEnum>(), Arg.Any<InboundSource>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoSpam_UnknownSender_MarksProcessed_SkipsPersistenceAndOrchestration()
    {
        var email = Email(InboxFolder);
        _clientAssignmentService.ResolveClientAsync(Arg.Any<ReceivedEmail>(), Arg.Any<CancellationToken>())
            .Returns(((Guid ClientId, EntityTypeEnum ClientType)?)null);

        await ProcessAsync(email);

        email.ProcessedAt.ShouldNotBeNull();
        await _unitOfWork.Received(1).CompleteAsync();
        await _analysisRepository.DidNotReceive().AddAsync(Arg.Any<InboundAnalysis>(), Arg.Any<CancellationToken>());
        await _actionOrchestrator.DidNotReceive().ExecuteAsync(
            Arg.Any<Guid>(), Arg.Any<InboundSource>(), Arg.Any<InboundAnalysis>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnalysisPresent_PersistsThenCommitsThenExecutesThenNotifies_InOrder()
    {
        var email = Email(InboxFolder);
        var analysis = Analysis(clientType: EntityTypeEnum.Customer);
        AnalysisServiceReturns(analysis);
        var orchestratorOutcome = new InboundActionOutcome(true, "done");
        _actionOrchestrator.ExecuteAsync(Arg.Any<Guid>(), Arg.Any<InboundSource>(), analysis, Arg.Any<CancellationToken>())
            .Returns(orchestratorOutcome);

        await ProcessAsync(email);

        Received.InOrder(() =>
        {
            _analysisRepository.AddAsync(analysis, Arg.Any<CancellationToken>());
            _unitOfWork.CompleteAsync();
            _actionOrchestrator.ExecuteAsync(Arg.Any<Guid>(), Arg.Any<InboundSource>(), analysis, Arg.Any<CancellationToken>());
            _analysisNotifier.NotifyAsync(
                Arg.Is<InboundSource>(s => s.SourceId == email.Id), analysis, orchestratorOutcome,
                Arg.Any<string?>(), Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task NonCustomerWithClientAndFromDate_BuildsPeriodSummary_PassesItToNotifier()
    {
        var email = Email(InboxFolder);
        var clientId = Guid.NewGuid();
        var fromDate = new DateOnly(2026, 8, 1);
        var untilDate = new DateOnly(2026, 8, 5);
        var analysis = Analysis(EntityTypeEnum.Employee, clientId, fromDate, untilDate);
        AnalysisServiceReturns(analysis);
        _periodLoadService.BuildSummaryAsync(clientId, fromDate, untilDate, Arg.Any<CancellationToken>())
            .Returns("3 shifts planned");

        await ProcessAsync(email);

        await _periodLoadService.Received(1).BuildSummaryAsync(clientId, fromDate, untilDate, Arg.Any<CancellationToken>());
        await _analysisNotifier.Received(1).NotifyAsync(
            Arg.Is<InboundSource>(s => s.SourceId == email.Id), analysis, Arg.Any<InboundActionOutcome?>(),
            "3 shifts planned", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NonCustomerWithNullUntilDate_BuildsPeriodSummary_UsingFromDateAsUntilDate()
    {
        var email = Email(InboxFolder);
        var clientId = Guid.NewGuid();
        var fromDate = new DateOnly(2026, 8, 1);
        var analysis = Analysis(EntityTypeEnum.Employee, clientId, fromDate, untilDate: null);
        AnalysisServiceReturns(analysis);

        await ProcessAsync(email);

        await _periodLoadService.Received(1).BuildSummaryAsync(clientId, fromDate, fromDate, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CustomerClientType_NeverBuildsPeriodSummary()
    {
        var email = Email(InboxFolder);
        var analysis = Analysis(EntityTypeEnum.Customer, Guid.NewGuid(), new DateOnly(2026, 8, 1));
        AnalysisServiceReturns(analysis);

        await ProcessAsync(email);

        await _periodLoadService.DidNotReceiveWithAnyArgs().BuildSummaryAsync(default, default, default, default);
    }

    [Test]
    public async Task OperationCanceledException_WhileTokenCancelled_IsRethrown()
    {
        var email = Email(InboxFolder);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _spamFilterService.ClassifyAsync(email, Arg.Any<CancellationToken>())
            .Returns<SpamFilterResult>(_ => throw new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() => ProcessAsync(email, cts.Token));
    }

    [Test]
    public async Task OperationCanceledException_WhileTokenNotCancelled_IsSwallowedNotRethrown()
    {
        var email = Email(InboxFolder);
        _spamFilterService.ClassifyAsync(email, Arg.Any<CancellationToken>())
            .Returns<SpamFilterResult>(_ => throw new OperationCanceledException());

        await ProcessAsync(email, CancellationToken.None);
    }

    [Test]
    public async Task AnalyzeAsyncThrows_IsLoggedOnly_ProcessedAtStaysNull()
    {
        var email = Email(InboxFolder);
        _intentAnalysisService.AnalyzeAsync(
            Arg.Any<Guid>(), Arg.Any<EntityTypeEnum>(), Arg.Any<InboundSource>(), Arg.Any<CancellationToken>())
            .Returns<InboundAnalysis>(_ => throw new InvalidOperationException("intent service down"));

        await ProcessAsync(email);

        email.ProcessedAt.ShouldBeNull();
    }

    [Test]
    public async Task ActionOrchestratorThrows_IsLoggedOnly_ButProcessedAtIsAlreadySet()
    {
        var email = Email(InboxFolder);
        var analysis = Analysis(EntityTypeEnum.Customer);
        AnalysisServiceReturns(analysis);
        _actionOrchestrator.ExecuteAsync(Arg.Any<Guid>(), Arg.Any<InboundSource>(), analysis, Arg.Any<CancellationToken>())
            .Returns<InboundActionOutcome?>(_ => throw new InvalidOperationException("orchestrator down"));

        await ProcessAsync(email);

        // ProcessedAt is set right after the analysis step (before AddAsync/CompleteAsync/ExecuteAsync),
        // so a failure downstream of that point leaves it non-null - unlike an AnalyzeAsync failure. This
        // means the class-level XML doc's blanket "leaves ProcessedAt null so it retries" guarantee only
        // holds for failures at or before the analysis step, not for failures in orchestration or notification.
        email.ProcessedAt.ShouldNotBeNull();
        await _analysisNotifier.DidNotReceiveWithAnyArgs().NotifyAsync(
            default!, default!, default, default, default);
    }

    [Test]
    public async Task ProcessEmailAsync_EmailAnalysisDisabled_SkipsClientResolutionAndAnalysis()
    {
        _settingsRepository.GetSetting(AppSettings.EMAIL_ANALYSIS_ENABLED)
            .Returns(new Klacks.Api.Domain.Models.Settings.Settings { Value = "false" });

        await ProcessAsync(Email(InboxFolder));

        await _clientAssignmentService.DidNotReceive().ResolveClientAsync(Arg.Any<ReceivedEmail>(), Arg.Any<CancellationToken>());
        await _intentAnalysisService.DidNotReceive().AnalyzeAsync(
            Arg.Any<Guid>(), Arg.Any<EntityTypeEnum>(), Arg.Any<InboundSource>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessEmailAsync_UnknownSender_SkipsAnalysisButStillMarksProcessed()
    {
        _clientAssignmentService.ResolveClientAsync(Arg.Any<ReceivedEmail>(), Arg.Any<CancellationToken>())
            .Returns(((Guid ClientId, EntityTypeEnum ClientType)?)null);
        var email = Email(InboxFolder);

        await ProcessAsync(email);

        await _intentAnalysisService.DidNotReceive().AnalyzeAsync(
            Arg.Any<Guid>(), Arg.Any<EntityTypeEnum>(), Arg.Any<InboundSource>(), Arg.Any<CancellationToken>());
        email.ProcessedAt.ShouldNotBeNull();
    }

    [Test]
    public async Task ClassifyFolderBatchedAsync_EmptyRepository_ReturnsZero_NoFurtherCalls()
    {
        const string sourceFolder = "ClientAssigned";
        _receivedEmailRepository.GetListByFolderAsync(sourceFolder, 0, 100).Returns([]);

        var moved = await _service.ClassifyFolderBatchedAsync(
            _receivedEmailRepository, _spamFilterService, _imapEmailService, sourceFolder, JunkFolder, CancellationToken.None);

        moved.ShouldBe(0);
        await _spamFilterService.DidNotReceiveWithAnyArgs().ClassifyAsync(default!, default);
        await _receivedEmailRepository.DidNotReceiveWithAnyArgs().MoveToFolderAsync(default, default!);
    }

    [Test]
    public async Task ClassifyFolderBatchedAsync_OneSpamAmongPage_MovesOnlyThatOne()
    {
        const string sourceFolder = "ClientAssigned";
        var spamEmail = new ReceivedEmail { Id = Guid.NewGuid(), ImapUid = 1 };
        var cleanEmail = new ReceivedEmail { Id = Guid.NewGuid(), ImapUid = 2 };
        _receivedEmailRepository.GetListByFolderAsync(sourceFolder, 0, 100)
            .Returns([spamEmail, cleanEmail]);
        _spamFilterService.ClassifyAsync(spamEmail, Arg.Any<CancellationToken>())
            .Returns(new SpamFilterResult { IsSpam = true });

        var moved = await _service.ClassifyFolderBatchedAsync(
            _receivedEmailRepository, _spamFilterService, _imapEmailService, sourceFolder, JunkFolder, CancellationToken.None);

        moved.ShouldBe(1);
        await _receivedEmailRepository.Received(1).MoveToFolderAsync(spamEmail.Id, JunkFolder);
        await _imapEmailService.Received(1).MoveEmailOnImapAsync(
            spamEmail.ImapUid, sourceFolder, JunkFolder, Arg.Any<CancellationToken>());
        await _receivedEmailRepository.DidNotReceive().MoveToFolderAsync(cleanEmail.Id, Arg.Any<string>());
    }

    [Test]
    public async Task ClassifyFolderBatchedAsync_SourceImapFolderSet_PrefersItOverSourceFolderParameter()
    {
        const string sourceFolder = "ClientAssigned";
        var withOwnSource = new ReceivedEmail { Id = Guid.NewGuid(), ImapUid = 1, SourceImapFolder = "OriginalInbox" };
        var withoutOwnSource = new ReceivedEmail { Id = Guid.NewGuid(), ImapUid = 2, SourceImapFolder = string.Empty };
        _receivedEmailRepository.GetListByFolderAsync(sourceFolder, 0, 100)
            .Returns([withOwnSource, withoutOwnSource]);
        _spamFilterService.ClassifyAsync(Arg.Any<ReceivedEmail>(), Arg.Any<CancellationToken>())
            .Returns(new SpamFilterResult { IsSpam = true });

        await _service.ClassifyFolderBatchedAsync(
            _receivedEmailRepository, _spamFilterService, _imapEmailService, sourceFolder, JunkFolder, CancellationToken.None);

        await _imapEmailService.Received(1).MoveEmailOnImapAsync(
            withOwnSource.ImapUid, "OriginalInbox", JunkFolder, Arg.Any<CancellationToken>());
        await _imapEmailService.Received(1).MoveEmailOnImapAsync(
            withoutOwnSource.ImapUid, sourceFolder, JunkFolder, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClassifyFolderBatchedAsync_FullPageOfHundred_FetchesSecondPage()
    {
        const string sourceFolder = "ClientAssigned";
        var fullPage = Enumerable.Range(0, 100)
            .Select(_ => new ReceivedEmail { Id = Guid.NewGuid(), ImapUid = 1 })
            .ToList();
        _receivedEmailRepository.GetListByFolderAsync(sourceFolder, 0, 100).Returns(fullPage);
        _receivedEmailRepository.GetListByFolderAsync(sourceFolder, 100, 100).Returns([]);

        await _service.ClassifyFolderBatchedAsync(
            _receivedEmailRepository, _spamFilterService, _imapEmailService, sourceFolder, JunkFolder, CancellationToken.None);

        await _receivedEmailRepository.Received(1).GetListByFolderAsync(sourceFolder, 100, 100);
    }

    [Test]
    public async Task ClassifyFolderBatchedAsync_PageBelowHundred_DoesNotFetchSecondPage()
    {
        const string sourceFolder = "ClientAssigned";
        var partialPage = Enumerable.Range(0, 50)
            .Select(_ => new ReceivedEmail { Id = Guid.NewGuid(), ImapUid = 1 })
            .ToList();
        _receivedEmailRepository.GetListByFolderAsync(sourceFolder, 0, 100).Returns(partialPage);

        await _service.ClassifyFolderBatchedAsync(
            _receivedEmailRepository, _spamFilterService, _imapEmailService, sourceFolder, JunkFolder, CancellationToken.None);

        await _receivedEmailRepository.DidNotReceive().GetListByFolderAsync(sourceFolder, 100, 100);
    }

    [Test]
    public async Task ClassifyFolderBatchedAsync_TokenAlreadyCancelled_NeverQueriesRepository()
    {
        const string sourceFolder = "ClientAssigned";
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var moved = await _service.ClassifyFolderBatchedAsync(
            _receivedEmailRepository, _spamFilterService, _imapEmailService, sourceFolder, JunkFolder, cts.Token);

        moved.ShouldBe(0);
        await _receivedEmailRepository.DidNotReceiveWithAnyArgs().GetListByFolderAsync(default!, default, default);
    }
}
