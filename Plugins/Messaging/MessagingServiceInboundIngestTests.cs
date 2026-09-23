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
using Klacks.Plugin.Messaging;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Infrastructure.Services;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    private const string TelegramSecretHeader = "X-Telegram-Bot-Api-Secret-Token";
    private const string WebhookSecret = "shh-secret";

    private IMessagingProviderRepository _providerRepository = null!;
    private IMessageRepository _messageRepository = null!;
    private IMessengerContactRepository _messengerContactRepository = null!;
    private IOwnerMessengerReader _ownerMessengerReader = null!;
    private IUserMessengerContactRepository _userMessengerContactRepository = null!;
    private IInboundMessengerObserver _inboundObserver = null!;
    private IInboundClientMessengerObserver _clientMessengerObserver = null!;
    private IPluginUnitOfWork _unitOfWork = null!;
    private IMessagingInboundActivityTracker _activityTracker = null!;
    private MemoryCache _logSuppressionCache = null!;
    private RecordingLogger<MessagingService> _logger = null!;
    private ServiceProvider _serviceProvider = null!;
    private IServiceScope _scope = null!;
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
        _clientMessengerObserver = Substitute.For<IInboundClientMessengerObserver>();

        _unitOfWork = Substitute.For<IPluginUnitOfWork>();
        _activityTracker = Substitute.For<IMessagingInboundActivityTracker>();
        _logSuppressionCache = new MemoryCache(new MemoryCacheOptions());
        _logger = new RecordingLogger<MessagingService>();

        var services = new ServiceCollection();
        services.AddLogging();
        new MessagingPluginRegistrar().RegisterServices(services, new ConfigurationBuilder().Build());
        _serviceProvider = services.BuildServiceProvider();
        _scope = _serviceProvider.CreateScope();

        _sut = BuildService(new[] { _inboundObserver }, new[] { _clientMessengerObserver });
    }

    private MessagingService BuildService(
        IEnumerable<IInboundMessengerObserver> inboundObservers,
        IEnumerable<IInboundClientMessengerObserver> clientMessengerObservers)
    {
        return new MessagingService(
            _providerRepository,
            _messageRepository,
            _messengerContactRepository,
            _ownerMessengerReader,
            _userMessengerContactRepository,
            Substitute.For<IAppUserDirectoryReader>(),
            inboundObservers,
            clientMessengerObservers,
            _logSuppressionCache,
            Substitute.For<IClientGroupReader>(),
            Substitute.For<IClientIdNumberReader>(),
            Substitute.For<IClientPhoneReader>(),
            _unitOfWork,
            Substitute.For<IPluginSettingsReader>(),
            _scope.ServiceProvider.GetRequiredService<MessagingProviderAdapterFactory>(),
            _activityTracker,
            _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _logSuppressionCache.Dispose();
        _scope.Dispose();
        _serviceProvider.Dispose();
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
        _activityTracker.DidNotReceive().RecordUnknownSender(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>());
        _activityTracker.DidNotReceive().RecordWebhookHit(Arg.Any<Guid>());
        _activityTracker.DidNotReceive().RecordSignatureRejection(Arg.Any<Guid>());
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
    /// Feeds the setup diagnosis its "arrived from an unknown sender" signal: without this, a
    /// discarded stranger leaves no trace anywhere the diagnosis can read.
    /// </summary>
    [Test]
    public async Task IngestInboundMessageAsync_UnknownSender_RecordsInActivityTracker()
    {
        _messageRepository.InboundExistsAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(false);

        await _sut.IngestInboundMessageAsync(
            ProviderName, new IncomingMessage(ExternalId, StrangerAlias, StrangerAlias, "wer bist du"));

        _activityTracker.Received(1).RecordUnknownSender(_provider.Id, StrangerAlias, StrangerAlias);
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
        _activityTracker.DidNotReceive().RecordUnknownSender(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>());
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

    /// <summary>
    /// The client-side counterpart to the user-observer tests above: a message resolved to a
    /// MessengerContact (known client) must reach IInboundClientMessengerObserver, never
    /// IInboundMessengerObserver, which only fires for a paired app user.
    /// </summary>
    [Test]
    public async Task IngestInboundMessageAsync_KnownContact_NotifiesClientObservers()
    {
        _messageRepository.InboundExistsAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(false);
        var clientId = Guid.NewGuid();
        GiveKnownContact(OwnerSlackAlias, clientId);

        var result = await _sut.IngestInboundMessageAsync(
            ProviderName, new IncomingMessage(ExternalId, OwnerSlackAlias, OwnerSlackAlias, "Wie weit bist du"));

        await _clientMessengerObserver.Received(1).OnInboundMessageAsync(
            Arg.Is<InboundClientMessengerMessage>(m =>
                m.MessageId == result!.Id
                && m.ClientId == clientId
                && m.Sender == OwnerSlackAlias
                && m.Content == "Wie weit bist du"
                && m.Channel == MessengerType.Slack.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IngestInboundMessageAsync_OwnerSender_DoesNotNotifyClientObservers()
    {
        _messageRepository.InboundExistsAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(false);
        GiveOwnerMessengers(new OwnerMessengerEntry { Type = MessengerType.Slack, Value = OwnerSlackAlias });

        await _sut.IngestInboundMessageAsync(
            ProviderName, new IncomingMessage(ExternalId, OwnerSlackAlias, OwnerSlackAlias, "Wie weit bist du"));

        await _clientMessengerObserver.DidNotReceive().OnInboundMessageAsync(
            Arg.Any<InboundClientMessengerMessage>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Mirrors IngestInboundMessageAsync_ThrowingObserver_StillReturnsTheStoredMessage for the
    /// client-side observer: a throwing client observer must not turn an already-persisted message
    /// into a failed ingest.
    /// </summary>
    [Test]
    public async Task IngestInboundMessageAsync_ThrowingClientObserver_StillReturnsTheStoredMessage()
    {
        _messageRepository.InboundExistsAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(false);
        GiveKnownContact(OwnerSlackAlias, Guid.NewGuid());
        _clientMessengerObserver
            .OnInboundMessageAsync(Arg.Any<InboundClientMessengerMessage>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("observer is broken"));

        var result = await _sut.IngestInboundMessageAsync(
            ProviderName, new IncomingMessage(ExternalId, OwnerSlackAlias, OwnerSlackAlias, "Wie weit bist du"));

        result.ShouldNotBeNull();
        await _messageRepository.Received(1).AddAsync(Arg.Any<Message>());
    }

    /// <summary>
    /// The setup diagnosis tells "never arrived" from "arrived, but rejected/discarded" apart only
    /// because a hit is recorded before the adapter even validates the signature or parses a payload.
    /// </summary>
    [Test]
    public async Task ProcessIncomingMessageAsync_EnabledProvider_RecordsWebhookHit()
    {
        _provider.ProviderType = "Telegram";
        _provider.WebhookSecret = WebhookSecret;
        var headers = new Dictionary<string, string> { [TelegramSecretHeader] = WebhookSecret };

        await _sut.ProcessIncomingMessageAsync(ProviderName, "{}", headers);

        _activityTracker.Received(1).RecordWebhookHit(_provider.Id);
    }

    /// <summary>
    /// A hit is only recorded for a webhook delivery that actually authenticated. A signature
    /// rejection must never also count as a hit, or the setup diagnosis could report "receiving
    /// traffic" for a provider that is only ever being probed with a wrong secret.
    /// </summary>
    [Test]
    public async Task ProcessIncomingMessageAsync_InvalidSignature_RecordsSignatureRejectionButNoHit()
    {
        _provider.ProviderType = "Telegram";
        _provider.WebhookSecret = WebhookSecret;
        var headers = new Dictionary<string, string> { [TelegramSecretHeader] = "wrong-secret" };

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => _sut.ProcessIncomingMessageAsync(ProviderName, "{}", headers));

        _activityTracker.Received(1).RecordSignatureRejection(_provider.Id);
        _activityTracker.DidNotReceive().RecordWebhookHit(_provider.Id);
    }

    [Test]
    public async Task ProcessIncomingMessageAsync_DisabledProvider_NeverRecordsWebhookHit()
    {
        _provider.ProviderType = "Telegram";
        _provider.WebhookSecret = WebhookSecret;
        _provider.IsEnabled = false;
        var headers = new Dictionary<string, string> { [TelegramSecretHeader] = WebhookSecret };

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => _sut.ProcessIncomingMessageAsync(ProviderName, "{}", headers));

        _activityTracker.DidNotReceive().RecordWebhookHit(Arg.Any<Guid>());
        _activityTracker.DidNotReceive().RecordSignatureRejection(Arg.Any<Guid>());
    }

    [Test]
    public async Task VerifySubscriptionChallengeAsync_WrongVerifyToken_RecordsSignatureRejectionButNoHit()
    {
        _provider.ProviderType = "WhatsApp";
        _provider.ConfigJson = "{\"VerifyToken\":\"correct-token\"}";

        var response = await _sut.VerifySubscriptionChallengeAsync(ProviderName, "wrong-token", "challenge-value");

        response.ShouldBeNull();
        _activityTracker.Received(1).RecordSignatureRejection(_provider.Id);
        _activityTracker.DidNotReceive().RecordWebhookHit(_provider.Id);
    }

    [Test]
    public async Task VerifySubscriptionChallengeAsync_CorrectVerifyToken_RecordsWebhookHit()
    {
        _provider.ProviderType = "WhatsApp";
        _provider.ConfigJson = "{\"VerifyToken\":\"correct-token\"}";

        var response = await _sut.VerifySubscriptionChallengeAsync(ProviderName, "correct-token", "challenge-value");

        response.ShouldBe("challenge-value");
        _activityTracker.Received(1).RecordWebhookHit(_provider.Id);
        _activityTracker.DidNotReceive().RecordSignatureRejection(_provider.Id);
    }

    /// <summary>
    /// C-1: the webhook route is always "telegram" while the provider has its own name. Authentication
    /// must resolve the provider the same way ProcessIncomingMessageAsync does, or every onboarding
    /// /start is rejected.
    /// </summary>
    [Test]
    public async Task AuthenticateWebhookAsync_ProviderNamedDifferentlyThanRoute_ResolvesByTypeAndRecordsHit()
    {
        const string route = "telegram";
        _provider.Name = "klacks-bot";
        _provider.ProviderType = "Telegram";
        _provider.WebhookSecret = WebhookSecret;
        _providerRepository.GetByNameAsync(route).Returns((MessagingProvider?)null);
        _providerRepository.GetEnabledAsync().Returns(new[] { _provider });
        var headers = new Dictionary<string, string> { [TelegramSecretHeader] = WebhookSecret };

        var authenticated = await _sut.AuthenticateWebhookAsync(route, "{}", headers);

        authenticated.ShouldBeTrue();
        _activityTracker.Received(1).RecordWebhookHit(_provider.Id);
        _activityTracker.DidNotReceive().RecordSignatureRejection(Arg.Any<Guid>());
    }

    [Test]
    public async Task AuthenticateWebhookAsync_WrongSecret_ReturnsFalseAndRecordsRejection()
    {
        _provider.ProviderType = "Telegram";
        _provider.WebhookSecret = WebhookSecret;
        var headers = new Dictionary<string, string> { [TelegramSecretHeader] = "wrong-secret" };

        var authenticated = await _sut.AuthenticateWebhookAsync(ProviderName, "{}", headers);

        authenticated.ShouldBeFalse();
        _activityTracker.Received(1).RecordSignatureRejection(_provider.Id);
        _activityTracker.DidNotReceive().RecordWebhookHit(Arg.Any<Guid>());
    }

    [Test]
    public async Task AuthenticateWebhookAsync_DisabledProvider_ReturnsFalseWithoutRecording()
    {
        _provider.ProviderType = "Telegram";
        _provider.WebhookSecret = WebhookSecret;
        _provider.IsEnabled = false;
        var headers = new Dictionary<string, string> { [TelegramSecretHeader] = WebhookSecret };

        var authenticated = await _sut.AuthenticateWebhookAsync(ProviderName, "{}", headers);

        authenticated.ShouldBeFalse();
        _activityTracker.DidNotReceive().RecordWebhookHit(Arg.Any<Guid>());
        _activityTracker.DidNotReceive().RecordSignatureRejection(Arg.Any<Guid>());
    }

    [Test]
    public async Task AuthenticateWebhookAsync_UnknownProvider_ReturnsFalseWithoutRecording()
    {
        _providerRepository.GetByNameAsync("telegram").Returns((MessagingProvider?)null);
        _providerRepository.GetEnabledAsync().Returns(Array.Empty<MessagingProvider>());
        var headers = new Dictionary<string, string> { [TelegramSecretHeader] = WebhookSecret };

        var authenticated = await _sut.AuthenticateWebhookAsync("telegram", "{}", headers);

        authenticated.ShouldBeFalse();
        _activityTracker.DidNotReceive().RecordWebhookHit(Arg.Any<Guid>());
        _activityTracker.DidNotReceive().RecordSignatureRejection(Arg.Any<Guid>());
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
