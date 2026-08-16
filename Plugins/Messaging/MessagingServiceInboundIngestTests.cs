// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests the shared inbound persist path used by both the webhook route and the poller.
/// Two things are load-bearing here. The duplicate guard: a polling cursor that slips would
/// otherwise replay messages, and every replay costs an LLM turn plus an outbound reply.
/// And the sender check (E61): an unknown sender is discarded and never answered, while the owner -
/// who has no MessengerContact row and structurally cannot have one - must still get through, or
/// the Slack owner bridge dies with it.
/// </summary>
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Infrastructure.Services;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class MessagingServiceInboundIngestTests
{
    private const string ProviderName = "slack";
    private const string ExternalId = "1785854300.000200";
    private const string OwnerSlackAlias = "U0BLRED0TK2";
    private const string StrangerAlias = "U9STRANGER";
    private const string PlannerAlias = "U4PLANNER";
    private const string PlannerUserId = "8f2c1d44-0b7e-4a1c-9d33-6c5a0e1b7f90";

    private IMessagingProviderRepository _providerRepository = null!;
    private IMessageRepository _messageRepository = null!;
    private IMessengerContactRepository _messengerContactRepository = null!;
    private IOwnerMessengerReader _ownerMessengerReader = null!;
    private IUserMessengerContactRepository _userMessengerContactRepository = null!;
    private IInboundMessengerObserver _inboundObserver = null!;
    private IPluginUnitOfWork _unitOfWork = null!;
    private MemoryCache _logSuppressionCache = null!;
    private RecordingLogger<MessagingService> _logger = null!;
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

        _ownerMessengerReader = Substitute.For<IOwnerMessengerReader>();
        GiveOwnerMessengers();

        _userMessengerContactRepository = Substitute.For<IUserMessengerContactRepository>();
        _inboundObserver = Substitute.For<IInboundMessengerObserver>();

        _unitOfWork = Substitute.For<IPluginUnitOfWork>();
        _logSuppressionCache = new MemoryCache(new MemoryCacheOptions());
        _logger = new RecordingLogger<MessagingService>();

        _sut = new MessagingService(
            _providerRepository,
            _messageRepository,
            _messengerContactRepository,
            _ownerMessengerReader,
            _userMessengerContactRepository,
            new[] { _inboundObserver },
            _logSuppressionCache,
            Substitute.For<IClientGroupReader>(),
            Substitute.For<IClientIdNumberReader>(),
            Substitute.For<IClientPhoneReader>(),
            _unitOfWork,
            Substitute.For<IPluginSettingsReader>(),
            null!,
            _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _logSuppressionCache.Dispose();
    }

    [Test]
    public async Task IngestInboundMessageAsync_KnownContact_PersistsAsInboundAndCommits()
    {
        GiveKnownContact(OwnerSlackAlias, Guid.NewGuid());
        _messageRepository.InboundExistsAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.IngestInboundMessageAsync(
            ProviderName, new IncomingMessage(ExternalId, OwnerSlackAlias, OwnerSlackAlias, "Wie weit bist du"));

        result.ShouldNotBeNull();
        result!.Direction.ShouldBe(MessageDirection.Inbound);
        result.Sender.ShouldBe(OwnerSlackAlias);
        result.ExternalMessageId.ShouldBe(ExternalId);
        await _messageRepository.Received(1).AddAsync(Arg.Any<Message>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task IngestInboundMessageAsync_AlreadyStored_ReturnsNullAndWritesNothing()
    {
        _messageRepository.InboundExistsAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.IngestInboundMessageAsync(
            ProviderName, new IncomingMessage(ExternalId, OwnerSlackAlias, OwnerSlackAlias, "Wie weit bist du"));

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
        GiveKnownContact(OwnerSlackAlias, clientId);

        var result = await _sut.IngestInboundMessageAsync(
            ProviderName, new IncomingMessage(ExternalId, OwnerSlackAlias, OwnerSlackAlias, "hallo"));

        result!.ClientId.ShouldBe(clientId);
    }

    [Test]
    public async Task IngestInboundMessageAsync_UnknownSender_IsDiscardedAndNeverStored()
    {
        _messageRepository.InboundExistsAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.IngestInboundMessageAsync(
            ProviderName, new IncomingMessage(ExternalId, StrangerAlias, StrangerAlias, "wer bist du"));

        result.ShouldBeNull();
        await _messageRepository.DidNotReceive().AddAsync(Arg.Any<Message>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    /// <summary>
    /// The regression guard for the Slack owner bridge. The owner is known through
    /// APP_OWNER_MESSENGERS only - MessengerContact.ClientId is a non-nullable FK to Client, so the
    /// owner cannot have a row there. Without this allowlist the bridge would go silent for good.
    /// </summary>
    [Test]
    public async Task IngestInboundMessageAsync_OwnerSender_IsAcceptedWithoutMessengerContact()
    {
        _messageRepository.InboundExistsAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(false);
        GiveOwnerMessengers(new OwnerMessengerEntry { Type = MessengerType.Slack, Value = OwnerSlackAlias });

        var result = await _sut.IngestInboundMessageAsync(
            ProviderName, new IncomingMessage(ExternalId, OwnerSlackAlias, OwnerSlackAlias, "Wie weit bist du"));

        result.ShouldNotBeNull();
        result!.Sender.ShouldBe(OwnerSlackAlias);
        result.ClientId.ShouldBeNull();
        await _messageRepository.Received(1).AddAsync(Arg.Any<Message>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task IngestInboundMessageAsync_OwnerAliasOfAnotherMessengerType_IsStillDiscarded()
    {
        _messageRepository.InboundExistsAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(false);
        GiveOwnerMessengers(new OwnerMessengerEntry { Type = MessengerType.Telegram, Value = OwnerSlackAlias });

        var result = await _sut.IngestInboundMessageAsync(
            ProviderName, new IncomingMessage(ExternalId, OwnerSlackAlias, OwnerSlackAlias, "hallo"));

        result.ShouldBeNull();
        await _messageRepository.DidNotReceive().AddAsync(Arg.Any<Message>());
    }

    [Test]
    public async Task IngestInboundMessageAsync_SameUnknownSender_IsLoggedOnlyOnce()
    {
        _messageRepository.InboundExistsAsync(_provider.Id, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        for (var i = 0; i < 5; i++)
        {
            await _sut.IngestInboundMessageAsync(
                ProviderName, new IncomingMessage($"{ExternalId}-{i}", StrangerAlias, StrangerAlias, "spam"));
        }

        WarningsMentioning(StrangerAlias).ShouldBe(1);
    }

    [Test]
    public async Task IngestInboundMessageAsync_DifferentUnknownSenders_AreLoggedSeparately()
    {
        _messageRepository.InboundExistsAsync(_provider.Id, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        await _sut.IngestInboundMessageAsync(
            ProviderName, new IncomingMessage($"{ExternalId}-a", StrangerAlias, StrangerAlias, "spam"));
        await _sut.IngestInboundMessageAsync(
            ProviderName, new IncomingMessage($"{ExternalId}-b", "U7OTHER", "U7OTHER", "spam"));

        WarningsMentioning(StrangerAlias).ShouldBe(1);
        WarningsMentioning("U7OTHER").ShouldBe(1);
    }

    /// <summary>
    /// Fail closed: without a MessengerType the sender cannot be checked against any known identity,
    /// so the message must not be stored - and therefore can never be answered.
    /// </summary>
    [Test]
    public async Task IngestInboundMessageAsync_UnmappableProviderType_IsDiscarded()
    {
        _provider.ProviderType = "NotAMessengerType";
        _messageRepository.InboundExistsAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.IngestInboundMessageAsync(
            ProviderName, new IncomingMessage(ExternalId, OwnerSlackAlias, OwnerSlackAlias, "hallo"));

        result.ShouldBeNull();
        await _messageRepository.DidNotReceive().AddAsync(Arg.Any<Message>());
    }

    /// <summary>
    /// The third source. A planner has no Client and is not the owner, so before UserMessengerContact
    /// was consulted here their reply was discarded and the escalation they answered never heard it.
    /// </summary>
    [Test]
    public async Task IngestInboundMessageAsync_PairedUserSender_IsAcceptedWithoutMessengerContact()
    {
        _messageRepository.InboundExistsAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(false);
        GivePairedUser(PlannerAlias, PlannerUserId);

        var result = await _sut.IngestInboundMessageAsync(
            ProviderName, new IncomingMessage(ExternalId, PlannerAlias, PlannerAlias, "ich uebernehme"));

        result.ShouldNotBeNull();
        result!.Sender.ShouldBe(PlannerAlias);
        result.ClientId.ShouldBeNull();
        await _messageRepository.Received(1).AddAsync(Arg.Any<Message>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task IngestInboundMessageAsync_PairedUserOfAnotherMessengerType_IsStillDiscarded()
    {
        _messageRepository.InboundExistsAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(false);
        _userMessengerContactRepository
            .GetByTypeAndValueAsync(MessengerType.Telegram, PlannerAlias, Arg.Any<CancellationToken>())
            .Returns(new UserMessengerContact { UserId = PlannerUserId, Type = MessengerType.Telegram, Value = PlannerAlias });

        var result = await _sut.IngestInboundMessageAsync(
            ProviderName, new IncomingMessage(ExternalId, PlannerAlias, PlannerAlias, "hallo"));

        result.ShouldBeNull();
        await _messageRepository.DidNotReceive().AddAsync(Arg.Any<Message>());
    }

    [Test]
    public async Task IngestInboundMessageAsync_PairedUserSender_NotifiesTheObserverWithTheUserId()
    {
        _messageRepository.InboundExistsAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(false);
        GivePairedUser(PlannerAlias, PlannerUserId);

        var result = await _sut.IngestInboundMessageAsync(
            ProviderName, new IncomingMessage(ExternalId, PlannerAlias, PlannerAlias, "ich uebernehme"));

        await _inboundObserver.Received(1).OnInboundMessageAsync(
            Arg.Is<InboundMessengerMessage>(m =>
                m.UserId == PlannerUserId
                && m.Sender == PlannerAlias
                && m.Content == "ich uebernehme"
                && m.MessageId == result!.Id
                && m.Channel == MessengerType.Slack.ToString()),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The owner bridge answers stored messages itself and the owner need not be an application user
    /// at all, so an owner message must not be announced as a user reply.
    /// </summary>
    [Test]
    public async Task IngestInboundMessageAsync_OwnerSenderWithoutPairedChannel_DoesNotNotifyTheObserver()
    {
        _messageRepository.InboundExistsAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(false);
        GiveOwnerMessengers(new OwnerMessengerEntry { Type = MessengerType.Slack, Value = OwnerSlackAlias });

        await _sut.IngestInboundMessageAsync(
            ProviderName, new IncomingMessage(ExternalId, OwnerSlackAlias, OwnerSlackAlias, "Wie weit bist du"));

        await _inboundObserver.DidNotReceive().OnInboundMessageAsync(
            Arg.Any<InboundMessengerMessage>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IngestInboundMessageAsync_UnknownSender_NeverNotifiesTheObserver()
    {
        _messageRepository.InboundExistsAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(false);

        await _sut.IngestInboundMessageAsync(
            ProviderName, new IncomingMessage(ExternalId, StrangerAlias, StrangerAlias, "wer bist du"));

        await _inboundObserver.DidNotReceive().OnInboundMessageAsync(
            Arg.Any<InboundMessengerMessage>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An observer is a listener, never a gate: the message is already committed when it runs, so a
    /// throwing observer may not turn a stored message into a failed ingest.
    /// </summary>
    [Test]
    public async Task IngestInboundMessageAsync_ThrowingObserver_StillReturnsTheStoredMessage()
    {
        _messageRepository.InboundExistsAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(false);
        GivePairedUser(PlannerAlias, PlannerUserId);
        _inboundObserver
            .OnInboundMessageAsync(Arg.Any<InboundMessengerMessage>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("observer is broken"));

        var result = await _sut.IngestInboundMessageAsync(
            ProviderName, new IncomingMessage(ExternalId, PlannerAlias, PlannerAlias, "ich uebernehme"));

        result.ShouldNotBeNull();
        await _messageRepository.Received(1).AddAsync(Arg.Any<Message>());
    }

    private void GivePairedUser(string value, string userId)
    {
        _userMessengerContactRepository
            .GetByTypeAndValueAsync(MessengerType.Slack, value, Arg.Any<CancellationToken>())
            .Returns(new UserMessengerContact { UserId = userId, Type = MessengerType.Slack, Value = value });
    }

    private void GiveKnownContact(string value, Guid clientId)
    {
        _messengerContactRepository
            .GetByTypeAndValueAsync(MessengerType.Slack, value, Arg.Any<CancellationToken>())
            .Returns(new MessengerContact { ClientId = clientId, Type = MessengerType.Slack, Value = value });
    }

    private void GiveOwnerMessengers(params OwnerMessengerEntry[] entries)
    {
        _ownerMessengerReader
            .GetAllAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<OwnerMessengerEntry>)entries);
    }

    private int WarningsMentioning(string sender)
    {
        return _logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains(sender));
    }
}
