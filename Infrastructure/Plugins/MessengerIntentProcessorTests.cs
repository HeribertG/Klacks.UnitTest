// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MessengerIntentProcessor — verifies the MESSENGER_ANALYSIS_ENABLED feature gate, the
/// full analyze/persist/execute/notify pipeline for a known client, the sender label (client name from
/// the database, then SenderDisplayName, then Sender), the
/// silent skip for an unknown or soft-deleted client, and the period-load digest parity with the email
/// adapter (built only for a dated intent of a non-customer, and IEmailPeriodLoadService resolved only in
/// that branch). The collaborators are served from a real ServiceCollection because the processor
/// resolves them lazily from its per-message scope; tests that must not touch the period-load service
/// leave it unregistered, so resolving it would fail the test. The clarification paths pin the wiring of
/// IClarificationCoordinator: request built from channel, sender and client type, analysis persisted
/// before the post-analysis check, a sent question skipping orchestrator and notification, an answer
/// analysis replacing the regular one (same instance and Id, passed through unchanged), joined notifier
/// contexts, the idempotency skip for an already analysed message, and coordinator failures degrading
/// to the regular path.
/// </summary>

using AppSettings = Klacks.Api.Application.Constants.Settings;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Interfaces.Inbound;
using Klacks.Api.Domain.Models.Inbound;
using Klacks.Api.Infrastructure.Plugins;
using Klacks.Plugin.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Plugins;

[TestFixture]
public class MessengerIntentProcessorTests
{
    private const string PeriodLoadDigest = "Group A: 12 shifts, 3 absences";
    private const string ClientName = "Anna Muster";

    private IClientRepository _clientRepository = null!;
    private ISettingsRepository _settingsRepository = null!;
    private IInboundIntentAnalysisService _intentAnalysisService = null!;
    private IInboundActionOrchestrator _actionOrchestrator = null!;
    private IInboundAnalysisRepository _analysisRepository = null!;
    private IInboundAnalysisNotifier _analysisNotifier = null!;
    private IEmailPeriodLoadService _periodLoadService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IClarificationCoordinator _clarificationCoordinator = null!;
    private ServiceProvider? _serviceProvider;
    private IServiceScope? _scope;

    [SetUp]
    public void SetUp()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _intentAnalysisService = Substitute.For<IInboundIntentAnalysisService>();
        _actionOrchestrator = Substitute.For<IInboundActionOrchestrator>();
        _analysisRepository = Substitute.For<IInboundAnalysisRepository>();
        _analysisNotifier = Substitute.For<IInboundAnalysisNotifier>();
        _periodLoadService = Substitute.For<IEmailPeriodLoadService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _clarificationCoordinator = Substitute.For<IClarificationCoordinator>();
        _clarificationCoordinator.BeforeAnalysisAsync(Arg.Any<ClarificationRequest>(), Arg.Any<CancellationToken>())
            .Returns(ClarificationPreAnalysis.None);
        _clarificationCoordinator.AfterAnalysisAsync(Arg.Any<ClarificationRequest>(), Arg.Any<InboundAnalysis>(), Arg.Any<CancellationToken>())
            .Returns(ClarificationPostAnalysis.Continue);
    }

    [TearDown]
    public void TearDown()
    {
        _scope?.Dispose();
        _serviceProvider?.Dispose();
    }

    private MessengerIntentProcessor CreateSut(bool registerPeriodLoadService = false)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _clientRepository);
        services.AddScoped(_ => _settingsRepository);
        services.AddScoped(_ => _intentAnalysisService);
        services.AddScoped(_ => _actionOrchestrator);
        services.AddScoped(_ => _analysisRepository);
        services.AddScoped(_ => _analysisNotifier);
        services.AddScoped(_ => _unitOfWork);
        services.AddScoped(_ => _clarificationCoordinator);
        if (registerPeriodLoadService)
        {
            services.AddScoped(_ => _periodLoadService);
        }

        _serviceProvider = services.BuildServiceProvider();
        _scope = _serviceProvider.CreateScope();

        return new MessengerIntentProcessor(
            _scope.ServiceProvider,
            Substitute.For<ILogger<MessengerIntentProcessor>>());
    }

    private static InboundClientMessengerMessage Message(Guid? clientId = null, string? senderDisplayName = "Jane Doe") => new(
        MessageId: Guid.NewGuid(),
        ClientId: clientId ?? Guid.NewGuid(),
        Channel: "Telegram",
        Sender: "12345",
        SenderDisplayName: senderDisplayName,
        Content: "I'm sick today",
        ReceivedAt: DateTime.UtcNow);

    private void SettingIs(string? value) =>
        _settingsRepository.GetSetting(AppSettings.MESSENGER_ANALYSIS_ENABLED)
            .Returns(value == null ? null : new Klacks.Api.Domain.Models.Settings.Settings { Value = value });

    private void ClientIs(Guid clientId, EntityTypeEnum clientType, string displayName = ClientName) =>
        _clientRepository.GetTypeAndDisplayNameAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(new ClientTypeAndDisplayName(clientType, displayName));

    private void AnalysisReturns(Guid clientId, EntityTypeEnum clientType, InboundAnalysis analysis)
    {
        ClientIs(clientId, clientType);
        _intentAnalysisService.AnalyzeAsync(clientId, clientType, Arg.Any<InboundSource>(), Arg.Any<CancellationToken>())
            .Returns(analysis);
    }

    [Test]
    public async Task Disabled_SkipsResolutionAndAnalysis()
    {
        SettingIs("false");
        var sut = CreateSut();

        await sut.ProcessAsync(Message(), CancellationToken.None);

        await _clientRepository.DidNotReceive().GetTypeAndDisplayNameAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _intentAnalysisService.DidNotReceive().AnalyzeAsync(
            Arg.Any<Guid>(), Arg.Any<EntityTypeEnum>(), Arg.Any<InboundSource>(), Arg.Any<CancellationToken>());
        await _analysisRepository.DidNotReceive().AddAsync(Arg.Any<InboundAnalysis>(), Arg.Any<CancellationToken>());
        await _actionOrchestrator.DidNotReceive().ExecuteAsync(
            Arg.Any<Guid>(), Arg.Any<InboundSource>(), Arg.Any<InboundAnalysis>(), Arg.Any<CancellationToken>());
        await _analysisNotifier.DidNotReceive().NotifyAsync(
            Arg.Any<InboundSource>(), Arg.Any<InboundAnalysis>(), Arg.Any<InboundActionOutcome?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Enabled_ResolvesClientTypeAndRunsFullPipeline()
    {
        SettingIs("true");
        var clientId = Guid.NewGuid();
        var message = Message(clientId);
        var analysis = new InboundAnalysis { ClientId = clientId, ClientType = EntityTypeEnum.Employee };
        var actionOutcome = new InboundActionOutcome(true, "Sick leave recorded");

        InboundSource? capturedSource = null;
        ClientIs(clientId, EntityTypeEnum.Employee);
        _intentAnalysisService.AnalyzeAsync(
            clientId, EntityTypeEnum.Employee, Arg.Do<InboundSource>(s => capturedSource = s), Arg.Any<CancellationToken>())
            .Returns(analysis);
        _actionOrchestrator.ExecuteAsync(clientId, Arg.Any<InboundSource>(), analysis, Arg.Any<CancellationToken>())
            .Returns(actionOutcome);
        var sut = CreateSut();

        await sut.ProcessAsync(message, CancellationToken.None);

        await _intentAnalysisService.Received(1).AnalyzeAsync(
            clientId, EntityTypeEnum.Employee, Arg.Any<InboundSource>(), Arg.Any<CancellationToken>());
        await _analysisRepository.Received(1).AddAsync(analysis, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).CompleteAsync();
        await _actionOrchestrator.Received(1).ExecuteAsync(clientId, Arg.Any<InboundSource>(), analysis, Arg.Any<CancellationToken>());
        await _analysisNotifier.Received(1).NotifyAsync(
            Arg.Any<InboundSource>(), analysis, actionOutcome, Arg.Is<string?>(s => s == null), Arg.Is<string?>(s => s == null), Arg.Any<CancellationToken>());

        Received.InOrder(async () =>
        {
            await _intentAnalysisService.AnalyzeAsync(
                clientId, EntityTypeEnum.Employee, Arg.Any<InboundSource>(), Arg.Any<CancellationToken>());
            await _analysisRepository.AddAsync(analysis, Arg.Any<CancellationToken>());
            await _unitOfWork.CompleteAsync();
            await _actionOrchestrator.ExecuteAsync(clientId, Arg.Any<InboundSource>(), analysis, Arg.Any<CancellationToken>());
            await _analysisNotifier.NotifyAsync(
                Arg.Any<InboundSource>(), analysis, actionOutcome, Arg.Is<string?>(s => s == null), Arg.Is<string?>(s => s == null), Arg.Any<CancellationToken>());
        });

        Assert.That(capturedSource, Is.Not.Null);
        Assert.That(capturedSource!.SourceKind, Is.EqualTo(InboundSourceKind.Messenger));
        Assert.That(capturedSource.Channel, Is.EqualTo($"{MessengerConstants.InboundChannelPrefix}Telegram"));
        Assert.That(capturedSource.Subject, Is.Null);
        Assert.That(capturedSource.Body, Is.EqualTo(message.Content));
        Assert.That(capturedSource.SenderDisplay, Is.EqualTo(ClientName));
    }

    [Test]
    public async Task Enabled_ClientWithoutName_FallsBackToSenderDisplayName()
    {
        SettingIs("true");
        var clientId = Guid.NewGuid();
        var message = Message(clientId);
        var analysis = new InboundAnalysis { ClientId = clientId, ClientType = EntityTypeEnum.Employee };

        InboundSource? capturedSource = null;
        ClientIs(clientId, EntityTypeEnum.Employee, displayName: string.Empty);
        _intentAnalysisService.AnalyzeAsync(
            clientId, EntityTypeEnum.Employee, Arg.Do<InboundSource>(s => capturedSource = s), Arg.Any<CancellationToken>())
            .Returns(analysis);
        var sut = CreateSut();

        await sut.ProcessAsync(message, CancellationToken.None);

        Assert.That(capturedSource, Is.Not.Null);
        Assert.That(capturedSource!.SenderDisplay, Is.EqualTo(message.SenderDisplayName));
    }

    [Test]
    public async Task Enabled_ClientWithoutNameAndMissingSenderDisplayName_FallsBackToSender()
    {
        SettingIs("true");
        var clientId = Guid.NewGuid();
        var message = Message(clientId, senderDisplayName: "   ");
        var analysis = new InboundAnalysis { ClientId = clientId, ClientType = EntityTypeEnum.Employee };

        InboundSource? capturedSource = null;
        ClientIs(clientId, EntityTypeEnum.Employee, displayName: "  ");
        _intentAnalysisService.AnalyzeAsync(
            clientId, EntityTypeEnum.Employee, Arg.Do<InboundSource>(s => capturedSource = s), Arg.Any<CancellationToken>())
            .Returns(analysis);
        var sut = CreateSut();

        await sut.ProcessAsync(message, CancellationToken.None);

        Assert.That(capturedSource, Is.Not.Null);
        Assert.That(capturedSource!.SenderDisplay, Is.EqualTo(message.Sender));
    }

    [Test]
    public async Task UnknownOrDeletedClient_SkipsAnalysisWithoutThrowing()
    {
        SettingIs("true");
        var message = Message();
        _clientRepository.GetTypeAndDisplayNameAsync(message.ClientId, Arg.Any<CancellationToken>())
            .Returns((ClientTypeAndDisplayName?)null);
        var sut = CreateSut();

        await sut.ProcessAsync(message, CancellationToken.None);

        await _intentAnalysisService.DidNotReceive().AnalyzeAsync(
            Arg.Any<Guid>(), Arg.Any<EntityTypeEnum>(), Arg.Any<InboundSource>(), Arg.Any<CancellationToken>());
        await _analysisRepository.DidNotReceive().AddAsync(Arg.Any<InboundAnalysis>(), Arg.Any<CancellationToken>());
        await _actionOrchestrator.DidNotReceive().ExecuteAsync(
            Arg.Any<Guid>(), Arg.Any<InboundSource>(), Arg.Any<InboundAnalysis>(), Arg.Any<CancellationToken>());
        await _analysisNotifier.DidNotReceive().NotifyAsync(
            Arg.Any<InboundSource>(), Arg.Any<InboundAnalysis>(), Arg.Any<InboundActionOutcome?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DatedIntentOfEmployee_BuildsPeriodLoadSummaryAndPassesItToTheNotifier()
    {
        SettingIs("true");
        var clientId = Guid.NewGuid();
        var fromDate = new DateOnly(2026, 10, 5);
        var untilDate = new DateOnly(2026, 10, 9);
        var analysis = new InboundAnalysis
        {
            ClientId = clientId,
            ClientType = EntityTypeEnum.Employee,
            FromDate = fromDate,
            UntilDate = untilDate
        };
        AnalysisReturns(clientId, EntityTypeEnum.Employee, analysis);
        _periodLoadService.BuildSummaryAsync(clientId, fromDate, untilDate, Arg.Any<CancellationToken>())
            .Returns(PeriodLoadDigest);
        var sut = CreateSut(registerPeriodLoadService: true);

        await sut.ProcessAsync(Message(clientId), CancellationToken.None);

        await _periodLoadService.Received(1).BuildSummaryAsync(clientId, fromDate, untilDate, Arg.Any<CancellationToken>());
        await _analysisNotifier.Received(1).NotifyAsync(
            Arg.Any<InboundSource>(), analysis, Arg.Any<InboundActionOutcome?>(), PeriodLoadDigest, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DatedIntentWithoutUntilDate_UsesFromDateAsBothBounds()
    {
        SettingIs("true");
        var clientId = Guid.NewGuid();
        var fromDate = new DateOnly(2026, 10, 5);
        var analysis = new InboundAnalysis
        {
            ClientId = clientId,
            ClientType = EntityTypeEnum.ExternEmp,
            FromDate = fromDate
        };
        AnalysisReturns(clientId, EntityTypeEnum.ExternEmp, analysis);
        _periodLoadService.BuildSummaryAsync(clientId, fromDate, fromDate, Arg.Any<CancellationToken>())
            .Returns(PeriodLoadDigest);
        var sut = CreateSut(registerPeriodLoadService: true);

        await sut.ProcessAsync(Message(clientId), CancellationToken.None);

        await _periodLoadService.Received(1).BuildSummaryAsync(clientId, fromDate, fromDate, Arg.Any<CancellationToken>());
        await _analysisNotifier.Received(1).NotifyAsync(
            Arg.Any<InboundSource>(), analysis, Arg.Any<InboundActionOutcome?>(), PeriodLoadDigest, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DatedIntentOfCustomer_DoesNotResolvePeriodLoadService()
    {
        SettingIs("true");
        var clientId = Guid.NewGuid();
        var analysis = new InboundAnalysis
        {
            ClientId = clientId,
            ClientType = EntityTypeEnum.Customer,
            FromDate = new DateOnly(2026, 10, 5)
        };
        AnalysisReturns(clientId, EntityTypeEnum.Customer, analysis);
        var sut = CreateSut(registerPeriodLoadService: false);

        await sut.ProcessAsync(Message(clientId), CancellationToken.None);

        await _analysisNotifier.Received(1).NotifyAsync(
            Arg.Any<InboundSource>(), analysis, Arg.Any<InboundActionOutcome?>(), Arg.Is<string?>(s => s == null), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UndatedIntentOfEmployee_DoesNotResolvePeriodLoadService()
    {
        SettingIs("true");
        var clientId = Guid.NewGuid();
        var analysis = new InboundAnalysis
        {
            ClientId = clientId,
            ClientType = EntityTypeEnum.Employee,
            FromDate = null
        };
        AnalysisReturns(clientId, EntityTypeEnum.Employee, analysis);
        var sut = CreateSut(registerPeriodLoadService: false);

        await sut.ProcessAsync(Message(clientId), CancellationToken.None);

        await _analysisNotifier.Received(1).NotifyAsync(
            Arg.Any<InboundSource>(), analysis, Arg.Any<InboundActionOutcome?>(), Arg.Is<string?>(s => s == null), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClarificationQuestionSent_SkipsOrchestratorPeriodLoadAndNotifier()
    {
        SettingIs("true");
        var clientId = Guid.NewGuid();
        var analysis = new InboundAnalysis { ClientId = clientId, ClientType = EntityTypeEnum.Employee, FromDate = new DateOnly(2026, 9, 23) };
        AnalysisReturns(clientId, EntityTypeEnum.Employee, analysis);
        _clarificationCoordinator.AfterAnalysisAsync(Arg.Any<ClarificationRequest>(), analysis, Arg.Any<CancellationToken>())
            .Returns(ClarificationPostAnalysis.Sent);
        var sut = CreateSut(registerPeriodLoadService: false);

        await sut.ProcessAsync(Message(clientId), CancellationToken.None);

        await _analysisRepository.Received(1).AddAsync(analysis, Arg.Any<CancellationToken>());
        await _actionOrchestrator.DidNotReceiveWithAnyArgs().ExecuteAsync(default, default!, default!, default);
        await _analysisNotifier.DidNotReceiveWithAnyArgs().NotifyAsync(default!, default!, default, default, default, default);
    }

    [Test]
    public async Task Analysis_IsPersistedAndCommitted_BeforeThePostAnalysisCheck_WithTheInMemoryInstance()
    {
        SettingIs("true");
        var clientId = Guid.NewGuid();
        var analysis = new InboundAnalysis { ClientId = clientId, ClientType = EntityTypeEnum.Employee, NeedsClarification = true };
        AnalysisReturns(clientId, EntityTypeEnum.Employee, analysis);
        var sut = CreateSut();

        await sut.ProcessAsync(Message(clientId), CancellationToken.None);

        await _clarificationCoordinator.Received(1).AfterAnalysisAsync(
            Arg.Any<ClarificationRequest>(), Arg.Is<InboundAnalysis>(a => ReferenceEquals(a, analysis)), Arg.Any<CancellationToken>());
        Received.InOrder(async () =>
        {
            await _clarificationCoordinator.BeforeAnalysisAsync(Arg.Any<ClarificationRequest>(), Arg.Any<CancellationToken>());
            await _intentAnalysisService.AnalyzeAsync(clientId, EntityTypeEnum.Employee, Arg.Any<InboundSource>(), Arg.Any<CancellationToken>());
            await _analysisRepository.AddAsync(analysis, Arg.Any<CancellationToken>());
            await _unitOfWork.CompleteAsync();
            await _clarificationCoordinator.AfterAnalysisAsync(Arg.Any<ClarificationRequest>(), analysis, Arg.Any<CancellationToken>());
            await _actionOrchestrator.ExecuteAsync(clientId, Arg.Any<InboundSource>(), analysis, Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task AnswerToAnOpenClarification_ReplacesTheRegularAnalysis_AndCarriesTheHistory()
    {
        SettingIs("true");
        var clientId = Guid.NewGuid();
        ClientIs(clientId, EntityTypeEnum.Employee);
        var answerAnalysisId = Guid.NewGuid();
        var answerAnalysis = new InboundAnalysis { Id = answerAnalysisId, ClientId = clientId, ClientType = EntityTypeEnum.Employee };
        _clarificationCoordinator.BeforeAnalysisAsync(Arg.Any<ClarificationRequest>(), Arg.Any<CancellationToken>())
            .Returns(ClarificationPreAnalysis.Answer(answerAnalysis, "💬 Answer to Klacksy's question"));
        var sut = CreateSut();

        await sut.ProcessAsync(Message(clientId), CancellationToken.None);

        await _intentAnalysisService.DidNotReceiveWithAnyArgs().AnalyzeAsync(default, default, default!, default);
        await _analysisRepository.Received(1).AddAsync(
            Arg.Is<InboundAnalysis>(a => ReferenceEquals(a, answerAnalysis)), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).CompleteAsync();
        await _clarificationCoordinator.DidNotReceiveWithAnyArgs().AfterAnalysisAsync(default!, default!, default);
        await _actionOrchestrator.Received(1).ExecuteAsync(
            clientId, Arg.Any<InboundSource>(), Arg.Is<InboundAnalysis>(a => ReferenceEquals(a, answerAnalysis)), Arg.Any<CancellationToken>());
        await _analysisNotifier.Received(1).NotifyAsync(
            Arg.Any<InboundSource>(), answerAnalysis, Arg.Any<InboundActionOutcome?>(), Arg.Any<string?>(),
            "💬 Answer to Klacksy's question", Arg.Any<CancellationToken>());
        answerAnalysis.Id.ShouldBe(answerAnalysisId);
    }

    [Test]
    public async Task UnclearAnswer_ReachesTheOrchestratorExactlyAsTheCoordinatorReturnedIt()
    {
        SettingIs("true");
        var clientId = Guid.NewGuid();
        ClientIs(clientId, EntityTypeEnum.Employee);
        var answerAnalysis = new InboundAnalysis
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ClientType = EntityTypeEnum.Employee,
            Intent = EmailIntent.WorkCancellation,
            NeedsClarification = true,
            Confidence = EmailConfidence.Low
        };
        _clarificationCoordinator.BeforeAnalysisAsync(Arg.Any<ClarificationRequest>(), Arg.Any<CancellationToken>())
            .Returns(ClarificationPreAnalysis.Answer(answerAnalysis, "💬 still unclear"));
        EmailConfidence? confidenceSeenByOrchestrator = null;
        _actionOrchestrator.ExecuteAsync(
                clientId, Arg.Any<InboundSource>(),
                Arg.Do<InboundAnalysis>(a => confidenceSeenByOrchestrator = a.Confidence), Arg.Any<CancellationToken>())
            .Returns(new InboundActionOutcome(false, "suggestion only"));
        var sut = CreateSut();

        await sut.ProcessAsync(Message(clientId), CancellationToken.None);

        confidenceSeenByOrchestrator.ShouldBe(EmailConfidence.Low);
        await _clarificationCoordinator.DidNotReceiveWithAnyArgs().AfterAnalysisAsync(default!, default!, default);
    }

    [Test]
    public async Task BothClarificationContexts_AreJoinedForTheNotifier()
    {
        SettingIs("true");
        var clientId = Guid.NewGuid();
        var analysis = new InboundAnalysis { ClientId = clientId, ClientType = EntityTypeEnum.Employee };
        AnalysisReturns(clientId, EntityTypeEnum.Employee, analysis);
        _clarificationCoordinator.BeforeAnalysisAsync(Arg.Any<ClarificationRequest>(), Arg.Any<CancellationToken>())
            .Returns(ClarificationPreAnalysis.Context("ℹ️ expired before"));
        _clarificationCoordinator.AfterAnalysisAsync(Arg.Any<ClarificationRequest>(), analysis, Arg.Any<CancellationToken>())
            .Returns(ClarificationPostAnalysis.ContinueWith("💡 Klacksy would ask back"));
        var sut = CreateSut();

        await sut.ProcessAsync(Message(clientId), CancellationToken.None);

        await _analysisNotifier.Received(1).NotifyAsync(
            Arg.Any<InboundSource>(), analysis, Arg.Any<InboundActionOutcome?>(), Arg.Any<string?>(),
            "ℹ️ expired before\n\n💡 Klacksy would ask back", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClarificationRequest_CarriesChannelSenderAndClientType()
    {
        SettingIs("true");
        var clientId = Guid.NewGuid();
        var message = Message(clientId);
        AnalysisReturns(clientId, EntityTypeEnum.ExternEmp, new InboundAnalysis { ClientId = clientId, ClientType = EntityTypeEnum.ExternEmp });
        ClarificationRequest? captured = null;
        _clarificationCoordinator.BeforeAnalysisAsync(Arg.Do<ClarificationRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(ClarificationPreAnalysis.None);
        var sut = CreateSut();

        await sut.ProcessAsync(message, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.ClientId.ShouldBe(clientId);
        captured.ClientType.ShouldBe(EntityTypeEnum.ExternEmp);
        captured.ReplyChannel.ShouldBe("Telegram");
        captured.SenderAddress.ShouldBe("12345");
        captured.EmailThread.ShouldBeNull();
        captured.Source.SourceKind.ShouldBe(InboundSourceKind.Messenger);
        captured.Source.SourceId.ShouldBe(message.MessageId);
    }

    [Test]
    public async Task MessageAlreadyAnalysed_IsSkippedBeforeTheClarificationCheckAndTheLlmCall()
    {
        SettingIs("true");
        var clientId = Guid.NewGuid();
        var message = Message(clientId);
        ClientIs(clientId, EntityTypeEnum.Employee);
        _analysisRepository.GetBySourceAsync(InboundSourceKind.Messenger, message.MessageId, Arg.Any<CancellationToken>())
            .Returns(new InboundAnalysis { SourceKind = InboundSourceKind.Messenger, SourceId = message.MessageId });
        var sut = CreateSut();

        await sut.ProcessAsync(message, CancellationToken.None);

        await _clarificationCoordinator.DidNotReceiveWithAnyArgs().BeforeAnalysisAsync(default!, default);
        await _intentAnalysisService.DidNotReceiveWithAnyArgs().AnalyzeAsync(default, default, default!, default);
        await _analysisRepository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _clarificationCoordinator.DidNotReceiveWithAnyArgs().AfterAnalysisAsync(default!, default!, default);
        await _actionOrchestrator.DidNotReceiveWithAnyArgs().ExecuteAsync(default, default!, default!, default);
        await _analysisNotifier.DidNotReceiveWithAnyArgs().NotifyAsync(default!, default!, default, default, default, default);
    }

    [Test]
    public async Task CoordinatorThrowsBeforeAnalysis_RegularPathRuns()
    {
        SettingIs("true");
        var clientId = Guid.NewGuid();
        var analysis = new InboundAnalysis { ClientId = clientId, ClientType = EntityTypeEnum.Employee };
        AnalysisReturns(clientId, EntityTypeEnum.Employee, analysis);
        _clarificationCoordinator.BeforeAnalysisAsync(Arg.Any<ClarificationRequest>(), Arg.Any<CancellationToken>())
            .Returns<ClarificationPreAnalysis>(_ => throw new InvalidOperationException("boom"));
        var sut = CreateSut();

        await sut.ProcessAsync(Message(clientId), CancellationToken.None);

        await _intentAnalysisService.Received(1).AnalyzeAsync(clientId, EntityTypeEnum.Employee, Arg.Any<InboundSource>(), Arg.Any<CancellationToken>());
        await _analysisRepository.Received(1).AddAsync(analysis, Arg.Any<CancellationToken>());
        await _actionOrchestrator.Received(1).ExecuteAsync(clientId, Arg.Any<InboundSource>(), analysis, Arg.Any<CancellationToken>());
        await _analysisNotifier.Received(1).NotifyAsync(
            Arg.Any<InboundSource>(), analysis, Arg.Any<InboundActionOutcome?>(), Arg.Any<string?>(),
            Arg.Is<string?>(s => s == null), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CoordinatorThrowsAfterAnalysis_OrchestratorAndNotifierStillRun()
    {
        SettingIs("true");
        var clientId = Guid.NewGuid();
        var analysis = new InboundAnalysis { ClientId = clientId, ClientType = EntityTypeEnum.Employee };
        AnalysisReturns(clientId, EntityTypeEnum.Employee, analysis);
        _clarificationCoordinator.BeforeAnalysisAsync(Arg.Any<ClarificationRequest>(), Arg.Any<CancellationToken>())
            .Returns(ClarificationPreAnalysis.Context("ℹ️ expired before"));
        _clarificationCoordinator.AfterAnalysisAsync(Arg.Any<ClarificationRequest>(), Arg.Any<InboundAnalysis>(), Arg.Any<CancellationToken>())
            .Returns<ClarificationPostAnalysis>(_ => throw new InvalidOperationException("boom"));
        var sut = CreateSut();

        await sut.ProcessAsync(Message(clientId), CancellationToken.None);

        await _actionOrchestrator.Received(1).ExecuteAsync(clientId, Arg.Any<InboundSource>(), analysis, Arg.Any<CancellationToken>());
        await _analysisNotifier.Received(1).NotifyAsync(
            Arg.Any<InboundSource>(), analysis, Arg.Any<InboundActionOutcome?>(), Arg.Any<string?>(),
            "ℹ️ expired before", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CancellationDuringTheClarificationCheck_IsNotSwallowed()
    {
        SettingIs("true");
        var clientId = Guid.NewGuid();
        ClientIs(clientId, EntityTypeEnum.Employee);
        using var cts = new CancellationTokenSource();
        _clarificationCoordinator.BeforeAnalysisAsync(Arg.Any<ClarificationRequest>(), Arg.Any<CancellationToken>())
            .Returns<ClarificationPreAnalysis>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });
        var sut = CreateSut();

        await Should.ThrowAsync<OperationCanceledException>(() => sut.ProcessAsync(Message(clientId), cts.Token));

        await _intentAnalysisService.DidNotReceiveWithAnyArgs().AnalyzeAsync(default, default, default!, default);
    }
}
