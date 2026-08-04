// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests the shared inbound persist path used by both the webhook route and the poller.
/// The duplicate guard is the load-bearing part: a polling cursor that slips would otherwise
/// replay messages, and every replay costs an LLM turn plus an outbound reply.
/// </summary>
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class MessagingServiceInboundIngestTests
{
    private const string ProviderName = "slack";
    private const string ExternalId = "1785854300.000200";

    private IMessagingProviderRepository _providerRepository = null!;
    private IMessageRepository _messageRepository = null!;
    private IMessengerContactRepository _messengerContactRepository = null!;
    private IPluginUnitOfWork _unitOfWork = null!;
    private MessagingService _sut = null!;
    private MessagingProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _provider = new MessagingProvider
        {
            Id = Guid.NewGuid(),
            Name = ProviderName,
            DisplayName = "Slack",
            ProviderType = "Slack",
            IsEnabled = true
        };

        _providerRepository = Substitute.For<IMessagingProviderRepository>();
        _providerRepository.GetByNameAsync(ProviderName).Returns(_provider);

        _messageRepository = Substitute.For<IMessageRepository>();
        _messengerContactRepository = Substitute.For<IMessengerContactRepository>();
        _unitOfWork = Substitute.For<IPluginUnitOfWork>();

        _sut = new MessagingService(
            _providerRepository,
            _messageRepository,
            _messengerContactRepository,
            Substitute.For<IClientGroupReader>(),
            Substitute.For<IClientIdNumberReader>(),
            Substitute.For<IClientPhoneReader>(),
            _unitOfWork,
            Substitute.For<IPluginSettingsReader>(),
            null!,
            NullLogger<MessagingService>.Instance);
    }

    [Test]
    public async Task IngestInboundMessageAsync_NewMessage_PersistsAsInboundAndCommits()
    {
        _messageRepository.InboundExistsAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.IngestInboundMessageAsync(
            ProviderName, new IncomingMessage(ExternalId, "U0BLRED0TK2", "U0BLRED0TK2", "Wie weit bist du"));

        result.ShouldNotBeNull();
        result!.Direction.ShouldBe(MessageDirection.Inbound);
        result.Sender.ShouldBe("U0BLRED0TK2");
        result.ExternalMessageId.ShouldBe(ExternalId);
        await _messageRepository.Received(1).AddAsync(Arg.Any<Message>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task IngestInboundMessageAsync_AlreadyStored_ReturnsNullAndWritesNothing()
    {
        _messageRepository.InboundExistsAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.IngestInboundMessageAsync(
            ProviderName, new IncomingMessage(ExternalId, "U0BLRED0TK2", "U0BLRED0TK2", "Wie weit bist du"));

        result.ShouldBeNull();
        await _messageRepository.DidNotReceive().AddAsync(Arg.Any<Message>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task IngestInboundMessageAsync_UnknownProvider_ReturnsNullInsteadOfThrowing()
    {
        _providerRepository.GetByNameAsync("nope").Returns((MessagingProvider?)null);
        _providerRepository.GetEnabledAsync().Returns(Array.Empty<MessagingProvider>());

        var result = await _sut.IngestInboundMessageAsync(
            "nope", new IncomingMessage(ExternalId, "U1", "U1", "hallo"));

        result.ShouldBeNull();
    }

    [Test]
    public async Task IngestInboundMessageAsync_KnownContact_AttachesTheClientId()
    {
        var clientId = Guid.NewGuid();
        _messageRepository.InboundExistsAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(false);
        _messengerContactRepository
            .GetByTypeAndValueAsync(MessengerType.Slack, "U0BLRED0TK2", Arg.Any<CancellationToken>())
            .Returns(new MessengerContact { ClientId = clientId, Type = MessengerType.Slack, Value = "U0BLRED0TK2" });

        var result = await _sut.IngestInboundMessageAsync(
            ProviderName, new IncomingMessage(ExternalId, "U0BLRED0TK2", "U0BLRED0TK2", "hallo"));

        result!.ClientId.ShouldBe(clientId);
    }
}
