// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MessengerIntentObserver — verifies the MESSENGER_ANALYSIS_ENABLED feature gate,
/// the full analyze/persist/execute/notify pipeline for a known client, and the silent skip for an
/// unknown or soft-deleted client. The collaborators are served from a real ServiceCollection because
/// the observer resolves them per message from its own scope (see the DI-cycle guard test below).
/// </summary>

using AppSettings = Klacks.Api.Application.Constants.Settings;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Inbound;
using Klacks.Api.Domain.Models.Inbound;
using Klacks.Api.Infrastructure.Plugins;
using Klacks.Plugin.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Plugins;

[TestFixture]
public class MessengerIntentObserverTests
{
    private IClientRepository _clientRepository = null!;
    private ISettingsRepository _settingsRepository = null!;
    private IInboundIntentAnalysisService _intentAnalysisService = null!;
    private IInboundActionOrchestrator _actionOrchestrator = null!;
    private IInboundAnalysisRepository _analysisRepository = null!;
    private IInboundAnalysisNotifier _analysisNotifier = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ServiceProvider _serviceProvider = null!;
    private MessengerIntentObserver _observer = null!;

    [SetUp]
    public void SetUp()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _intentAnalysisService = Substitute.For<IInboundIntentAnalysisService>();
        _actionOrchestrator = Substitute.For<IInboundActionOrchestrator>();
        _analysisRepository = Substitute.For<IInboundAnalysisRepository>();
        _analysisNotifier = Substitute.For<IInboundAnalysisNotifier>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        var services = new ServiceCollection();
        services.AddScoped(_ => _clientRepository);
        services.AddScoped(_ => _settingsRepository);
        services.AddScoped(_ => _intentAnalysisService);
        services.AddScoped(_ => _actionOrchestrator);
        services.AddScoped(_ => _analysisRepository);
        services.AddScoped(_ => _analysisNotifier);
        services.AddScoped(_ => _unitOfWork);
        _serviceProvider = services.BuildServiceProvider();

        _observer = new MessengerIntentObserver(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<ILogger<MessengerIntentObserver>>());
    }

    [TearDown]
    public void TearDown() => _serviceProvider.Dispose();

    [Test]
    public void Constructor_TakesNoKernelServices_SoMessagingServiceCannotCloseADiCycle()
    {
        var parameterTypes = typeof(MessengerIntentObserver).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToList();

        Assert.That(parameterTypes, Is.EquivalentTo(new[]
        {
            typeof(IServiceScopeFactory),
            typeof(ILogger<MessengerIntentObserver>)
        }));
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

    [Test]
    public async Task Disabled_SkipsResolutionAndAnalysis()
    {
        SettingIs("false");
        var message = Message();

        await _observer.OnInboundMessageAsync(message);

        await _clientRepository.DidNotReceive().GetTypeAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _intentAnalysisService.DidNotReceive().AnalyzeAsync(
            Arg.Any<Guid>(), Arg.Any<EntityTypeEnum>(), Arg.Any<InboundSource>(), Arg.Any<CancellationToken>());
        await _analysisRepository.DidNotReceive().AddAsync(Arg.Any<InboundAnalysis>(), Arg.Any<CancellationToken>());
        await _actionOrchestrator.DidNotReceive().ExecuteAsync(
            Arg.Any<Guid>(), Arg.Any<InboundSource>(), Arg.Any<InboundAnalysis>(), Arg.Any<CancellationToken>());
        await _analysisNotifier.DidNotReceive().NotifyAsync(
            Arg.Any<InboundSource>(), Arg.Any<InboundAnalysis>(), Arg.Any<InboundActionOutcome?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
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
        _clientRepository.GetTypeAsync(clientId, Arg.Any<CancellationToken>()).Returns(EntityTypeEnum.Employee);
        _intentAnalysisService.AnalyzeAsync(
            clientId, EntityTypeEnum.Employee, Arg.Do<InboundSource>(s => capturedSource = s), Arg.Any<CancellationToken>())
            .Returns(analysis);
        _actionOrchestrator.ExecuteAsync(clientId, Arg.Any<InboundSource>(), analysis, Arg.Any<CancellationToken>())
            .Returns(actionOutcome);

        await _observer.OnInboundMessageAsync(message);

        await _intentAnalysisService.Received(1).AnalyzeAsync(
            clientId, EntityTypeEnum.Employee, Arg.Any<InboundSource>(), Arg.Any<CancellationToken>());
        await _analysisRepository.Received(1).AddAsync(analysis, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).CompleteAsync();
        await _actionOrchestrator.Received(1).ExecuteAsync(clientId, Arg.Any<InboundSource>(), analysis, Arg.Any<CancellationToken>());
        await _analysisNotifier.Received(1).NotifyAsync(
            Arg.Any<InboundSource>(), analysis, actionOutcome, null, Arg.Any<CancellationToken>());

        Received.InOrder(async () =>
        {
            await _intentAnalysisService.AnalyzeAsync(
                clientId, EntityTypeEnum.Employee, Arg.Any<InboundSource>(), Arg.Any<CancellationToken>());
            await _analysisRepository.AddAsync(analysis, Arg.Any<CancellationToken>());
            await _actionOrchestrator.ExecuteAsync(clientId, Arg.Any<InboundSource>(), analysis, Arg.Any<CancellationToken>());
            await _analysisNotifier.NotifyAsync(
                Arg.Any<InboundSource>(), analysis, actionOutcome, null, Arg.Any<CancellationToken>());
        });

        Assert.That(capturedSource, Is.Not.Null);
        Assert.That(capturedSource!.SourceKind, Is.EqualTo(InboundSourceKind.Messenger));
        Assert.That(capturedSource.Channel, Is.EqualTo($"{MessengerConstants.InboundChannelPrefix}Telegram"));
        Assert.That(capturedSource.Subject, Is.Null);
        Assert.That(capturedSource.Body, Is.EqualTo(message.Content));
        Assert.That(capturedSource.SenderDisplay, Is.EqualTo(message.SenderDisplayName));
    }

    [Test]
    public async Task Enabled_MissingSenderDisplayName_FallsBackToSender()
    {
        SettingIs("true");
        var clientId = Guid.NewGuid();
        var message = Message(clientId, senderDisplayName: "   ");
        var analysis = new InboundAnalysis { ClientId = clientId, ClientType = EntityTypeEnum.Employee };

        InboundSource? capturedSource = null;
        _clientRepository.GetTypeAsync(clientId, Arg.Any<CancellationToken>()).Returns(EntityTypeEnum.Employee);
        _intentAnalysisService.AnalyzeAsync(
            clientId, EntityTypeEnum.Employee, Arg.Do<InboundSource>(s => capturedSource = s), Arg.Any<CancellationToken>())
            .Returns(analysis);

        await _observer.OnInboundMessageAsync(message);

        Assert.That(capturedSource, Is.Not.Null);
        Assert.That(capturedSource!.SenderDisplay, Is.EqualTo(message.Sender));
    }

    [Test]
    public async Task UnknownOrDeletedClient_SkipsAnalysisWithoutThrowing()
    {
        SettingIs("true");
        var message = Message();
        _clientRepository.GetTypeAsync(message.ClientId, Arg.Any<CancellationToken>()).Returns((EntityTypeEnum?)null);

        await _observer.OnInboundMessageAsync(message);

        await _intentAnalysisService.DidNotReceive().AnalyzeAsync(
            Arg.Any<Guid>(), Arg.Any<EntityTypeEnum>(), Arg.Any<InboundSource>(), Arg.Any<CancellationToken>());
        await _analysisRepository.DidNotReceive().AddAsync(Arg.Any<InboundAnalysis>(), Arg.Any<CancellationToken>());
        await _actionOrchestrator.DidNotReceive().ExecuteAsync(
            Arg.Any<Guid>(), Arg.Any<InboundSource>(), Arg.Any<InboundAnalysis>(), Arg.Any<CancellationToken>());
        await _analysisNotifier.DidNotReceive().NotifyAsync(
            Arg.Any<InboundSource>(), Arg.Any<InboundAnalysis>(), Arg.Any<InboundActionOutcome?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
