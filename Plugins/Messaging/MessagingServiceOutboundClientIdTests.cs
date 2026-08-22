// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests that the outbound send path resolves and persists ClientId the same way the inbound path
/// already does (MessengerContact lookup by provider type + recipient value). Without this, every
/// outbound client message misclassifies as scope=Internal once the messaging-workplace filter uses
/// ClientId to separate client traffic from the owner Slack bridge.
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
public class MessagingServiceOutboundClientIdTests
{
    private const string EmptyConfig = "{}";
    private const string Recipient = "12345";

    private IMessagingProviderRepository _providerRepository = null!;
    private IMessageRepository _messageRepository = null!;
    private IMessengerContactRepository _messengerContactRepository = null!;
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
        _messengerContactRepository = Substitute.For<IMessengerContactRepository>();
        _unitOfWork = Substitute.For<IPluginUnitOfWork>();
        _logSuppressionCache = new MemoryCache(new MemoryCacheOptions());

        _sut = new MessagingService(
            _providerRepository,
            _messageRepository,
            _messengerContactRepository,
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
    public async Task SendMessageAsync_RecipientKnownAsClientContact_PersistsWithClientId()
    {
        GiveProvider(MessagingConstants.ProviderSms);
        var clientId = Guid.NewGuid();
        _messengerContactRepository
            .GetByTypeAndValueAsync(MessengerType.Sms, Recipient, Arg.Any<CancellationToken>())
            .Returns(new MessengerContact { Id = Guid.NewGuid(), ClientId = clientId, Type = MessengerType.Sms, Value = Recipient });

        await _sut.SendMessageAsync(MessagingConstants.ProviderSms, new SendMessageRequest(Recipient, "Dienst 06:00"));

        await _messageRepository.Received(1).AddAsync(Arg.Is<Message>(m => m.ClientId == clientId));
    }

    [Test]
    public async Task SendMessageAsync_RecipientNotAKnownContact_PersistsWithNullClientId()
    {
        GiveProvider(MessagingConstants.ProviderSms);
        _messengerContactRepository
            .GetByTypeAndValueAsync(MessengerType.Sms, Recipient, Arg.Any<CancellationToken>())
            .Returns((MessengerContact?)null);

        await _sut.SendMessageAsync(MessagingConstants.ProviderSms, new SendMessageRequest(Recipient, "Nachricht"));

        await _messageRepository.Received(1).AddAsync(Arg.Is<Message>(m => m.ClientId == null));
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
