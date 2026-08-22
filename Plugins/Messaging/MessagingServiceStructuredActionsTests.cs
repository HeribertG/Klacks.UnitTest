// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests the structured-action transport on the outbound path (E65). Requesting actions from a
/// provider that cannot carry them is refused and recorded as a failed message rather than sent as
/// bare text - a recipient who receives the question without the choices cannot answer it, and the
/// silent variant would leave no audit record of the attempt.
/// Uses the plugin's real DI registration so the adapters under test are the production ones. No
/// call reaches the network: the refusal returns before the adapter is asked, and the positive
/// control uses an empty config that every adapter rejects before its first HTTP call.
/// </summary>
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class MessagingServiceStructuredActionsTests
{
    private const string EmptyConfig = "{}";
    private const string Recipient = "12345";

    private static readonly IReadOnlyList<MessageAction> Actions = new[]
    {
        new MessageAction("accept", "Ich uebernehme"),
        new MessageAction("decline", "Ich kann nicht"),
    };

    private IMessagingProviderRepository _providerRepository = null!;
    private IMessageRepository _messageRepository = null!;
    private IPluginUnitOfWork _unitOfWork = null!;
    private ServiceProvider _serviceProvider = null!;
    private IServiceScope _scope = null!;
    private MemoryCache _logSuppressionCache = null!;
    private MessagingService _sut = null!;

    [SetUp]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        new MessagingPluginRegistrar().RegisterServices(services, new ConfigurationBuilder().Build());
        _serviceProvider = services.BuildServiceProvider();
        _scope = _serviceProvider.CreateScope();

        _providerRepository = Substitute.For<IMessagingProviderRepository>();
        _messageRepository = Substitute.For<IMessageRepository>();
        _unitOfWork = Substitute.For<IPluginUnitOfWork>();
        _logSuppressionCache = new MemoryCache(new MemoryCacheOptions());

        _sut = new MessagingService(
            _providerRepository,
            _messageRepository,
            Substitute.For<IMessengerContactRepository>(),
            Substitute.For<IOwnerMessengerReader>(),
            Substitute.For<IUserMessengerContactRepository>(),
            Substitute.For<IAppUserDirectoryReader>(),
            Array.Empty<IInboundMessengerObserver>(),
            _logSuppressionCache,
            Substitute.For<IClientGroupReader>(),
            Substitute.For<IClientIdNumberReader>(),
            Substitute.For<IClientPhoneReader>(),
            _unitOfWork,
            Substitute.For<IPluginSettingsReader>(),
            _scope.ServiceProvider.GetRequiredService<MessagingProviderAdapterFactory>(),
            NullLogger<MessagingService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _logSuppressionCache.Dispose();
        _scope.Dispose();
        _serviceProvider.Dispose();
    }

    [Test]
    public async Task SendMessageAsync_ActionsOnASendOnlyProvider_IsRefused()
    {
        GiveProvider(MessagingConstants.ProviderSms);

        var result = await _sut.SendMessageAsync(
            MessagingConstants.ProviderSms,
            new SendMessageRequest(Recipient, "Dienst 06:00", Actions: Actions));

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("structured actions");
    }

    [Test]
    public async Task SendMessageAsync_RefusedActions_AreStillRecordedAsAFailedMessage()
    {
        GiveProvider(MessagingConstants.ProviderSms);

        await _sut.SendMessageAsync(
            MessagingConstants.ProviderSms,
            new SendMessageRequest(Recipient, "Dienst 06:00", Actions: Actions));

        await _messageRepository.Received(1).AddAsync(Arg.Is<Message>(m =>
            m.Direction == MessageDirection.Outbound
            && m.Status == MessageStatus.Failed
            && m.Recipient == Recipient
            && m.ErrorMessage != null
            && m.ErrorMessage.Contains("structured actions")));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    /// <summary>
    /// Positive control: on a provider that can carry actions the guard must not fire. The attempt
    /// reaches the adapter, which then rejects the empty configuration - a different error entirely.
    /// </summary>
    [Test]
    public async Task SendMessageAsync_ActionsOnAReceivingProvider_ReachTheAdapter()
    {
        GiveProvider(MessagingConstants.ProviderTelegram);

        var result = await _sut.SendMessageAsync(
            MessagingConstants.ProviderTelegram,
            new SendMessageRequest(Recipient, "Dienst 06:00", Actions: Actions));

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldNotContain("structured actions");
        result.ErrorMessage!.ShouldContain("BotToken");
    }

    [Test]
    public async Task SendMessageAsync_WithoutActions_IsUnaffectedOnASendOnlyProvider()
    {
        GiveProvider(MessagingConstants.ProviderSms);

        var result = await _sut.SendMessageAsync(
            MessagingConstants.ProviderSms,
            new SendMessageRequest(Recipient, "Nur eine Mitteilung"));

        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldNotContain("structured actions");
    }

    private void GiveProvider(string providerType)
    {
        var provider = new MessagingProvider
        {
            Id = Guid.NewGuid(),
            Name = providerType,
            DisplayName = providerType,
            ProviderType = providerType,
            ConfigJson = EmptyConfig,
            IsEnabled = true
        };

        _providerRepository.GetByNameAsync(providerType).Returns(provider);
    }
}
