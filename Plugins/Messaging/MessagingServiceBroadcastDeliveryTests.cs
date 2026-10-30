// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The delivery behind send_broadcast with the REAL MessagingService, address rule and Telegram adapter on a stubbed HTTP handler
/// (no request leaves the process): a provider that refuses one recipient and a transport failure at another do not stop the run,
/// every recipient ends in one message row, and the result counts sent, failed and skipped. Sends go to exactly the given addresses
/// without resolving again, a disabled provider is refused, and a client's contact wins over the mobile number. Each recipient is
/// claimed with a committed Pending row before its send, a failed commit stops the run before the next send, and a run in progress
/// keeps a second one from sending or recording anything. The run lock is released in every case.
/// </summary>

using System.Net;
using System.Text;
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Application.Services;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Infrastructure.Services;
using Klacks.Plugin.Messaging.Infrastructure.Services.Providers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class MessagingServiceBroadcastDeliveryTests
{
    private const string Text = "Hallo Team";
    private const string Config = "{\"BotToken\":\"test-token\"}";
    private const string Accepted = "chat-ok";
    private const string Refused = "chat-refused";
    private const string Broken = "chat-broken";
    private const string SuccessBody = "{\"ok\":true,\"result\":{\"message_id\":1}}";

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        public Action? BeforeSend { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            BeforeSend?.Invoke();
            Bodies.Add(body);

            if (body.Contains(Broken, StringComparison.Ordinal))
            {
                throw new HttpRequestException("connection lost");
            }

            return body.Contains(Refused, StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SuccessBody, Encoding.UTF8) };
        }
    }

    private StubHandler _handler = null!;
    private IMessagingProviderRepository _providers = null!;
    private IMessageRepository _messages = null!;
    private IMessengerContactRepository _contacts = null!;
    private IClientPhoneReader _phones = null!;
    private IPluginUnitOfWork _unitOfWork = null!;
    private IPluginSettingsReader _settings = null!;
    private IBroadcastRunLock _runLock = null!;
    private IAsyncDisposable _runHold = null!;
    private MessagingProvider _provider = null!;
    private ServiceProvider _services = null!;
    private MemoryCache _cache = null!;
    private List<Message> _stored = null!;
    private MessagingService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _handler = new StubHandler();
        _cache = new MemoryCache(new MemoryCacheOptions());
        var adapter = new TelegramMessagingProvider(new HttpClient(_handler), _cache, NullLogger<TelegramMessagingProvider>.Instance);
        _services = new ServiceCollection().AddSingleton(adapter).BuildServiceProvider();

        _provider = new MessagingProvider
        {
            Id = Guid.NewGuid(),
            Name = "team-bot",
            ProviderType = MessagingConstants.ProviderTelegram,
            ConfigJson = Config,
            IsEnabled = true
        };
        _providers = Substitute.For<IMessagingProviderRepository>();
        _providers.GetByNameAsync(_provider.Name).Returns(_provider);

        _stored = [];
        _messages = Substitute.For<IMessageRepository>();
        _messages.When(repository => repository.AddAsync(Arg.Any<Message>())).Do(call => _stored.Add(call.Arg<Message>()));
        _contacts = Substitute.For<IMessengerContactRepository>();
        _phones = Substitute.For<IClientPhoneReader>();
        _unitOfWork = Substitute.For<IPluginUnitOfWork>();
        _settings = Substitute.For<IPluginSettingsReader>();
        _settings.GetSettingAsync(MessagingRateLimitConstants.SettingBroadcastPacingMs).Returns("0");
        _runHold = Substitute.For<IAsyncDisposable>();
        _runLock = Substitute.For<IBroadcastRunLock>();
        _runLock.TryAcquireAsync(Arg.Any<CancellationToken>()).Returns(_runHold);

        _sut = new MessagingService(
            _providers,
            _messages,
            _contacts,
            Substitute.For<IOwnerMessengerReader>(),
            Substitute.For<IUserMessengerContactRepository>(),
            Substitute.For<IAppUserDirectoryReader>(),
            Array.Empty<IInboundMessengerObserver>(),
            Array.Empty<IInboundClientMessengerObserver>(),
            _cache,
            Substitute.For<IClientGroupReader>(),
            Substitute.For<IClientIdNumberReader>(),
            new BroadcastRecipientResolver(_contacts, _phones),
            _unitOfWork,
            _settings,
            new MessagingProviderAdapterFactory(_services),
            Substitute.For<IMessagingInboundActivityTracker>(),
            _runLock,
            NullLogger<MessagingService>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _runHold.DisposeAsync();
        _cache.Dispose();
        _services.Dispose();
        _handler.Dispose();
    }

    private BroadcastRecipientSet Set(int skipped, params string[] addresses) =>
        new(_provider.Id, _provider.Name, addresses.Select(a => new BroadcastRecipient(Guid.NewGuid(), a, null, false)).ToList(), skipped);

    [Test]
    public async Task AFailureAtOneRecipient_DoesNotStopTheOthers()
    {
        var result = await _sut.SendBroadcastToRecipientsAsync(
            Set(2, Refused, Broken, Accepted), Text, MessagingConstants.DefaultContentType);

        result.Sent.ShouldBe(1);
        result.Failed.ShouldBe(2);
        result.SkippedNoContact.ShouldBe(2);
        result.Total.ShouldBe(5);
        _handler.Bodies.Count.ShouldBe(3);
        _handler.Bodies.ShouldContain(body => body.Contains(Accepted, StringComparison.Ordinal));
    }

    [Test]
    public async Task EveryRecipient_EndsInOneMessageRow_AndTheRunIsCommitted()
    {
        await _sut.SendBroadcastToRecipientsAsync(Set(0, Refused, Broken, Accepted), Text, MessagingConstants.DefaultContentType);

        _stored.Count.ShouldBe(3);
        _stored.Select(message => message.BroadcastId).Distinct().ShouldHaveSingleItem().ShouldNotBeNull();
        _stored.Single(message => message.Recipient == Accepted).Status.ShouldBe(MessageStatus.Sent);
        _stored.Count(message => message.Status == MessageStatus.Failed).ShouldBe(2);
        await _unitOfWork.Received(7).CompleteAsync();
    }

    [Test]
    public async Task EveryRecipient_IsClaimedDurably_BeforeItsSend()
    {
        var commits = 0;
        var seenAtSend = new List<(int Commits, MessageStatus Status)>();
        _unitOfWork.When(unit => unit.CompleteAsync()).Do(_ => commits++);
        _handler.BeforeSend = () => seenAtSend.Add((commits, _stored[^1].Status));

        await _sut.SendBroadcastToRecipientsAsync(Set(0, Accepted, Accepted), Text, MessagingConstants.DefaultContentType);

        seenAtSend.ShouldBe([(1, MessageStatus.Pending), (3, MessageStatus.Pending)]);
        _stored.ShouldAllBe(message => message.Status == MessageStatus.Sent);
    }

    [Test]
    public async Task AFailedClaim_StopsTheRunBeforeTheNextSend_AndReleasesTheLock()
    {
        var commits = 0;
        _unitOfWork.CompleteAsync().Returns(_ =>
            ++commits == 3 ? Task.FromException(new TimeoutException("database gone")) : Task.CompletedTask);

        await Should.ThrowAsync<TimeoutException>(() =>
            _sut.SendBroadcastToRecipientsAsync(Set(0, Accepted, Accepted), Text, MessagingConstants.DefaultContentType));

        _handler.Bodies.Count.ShouldBe(1);
        _stored[0].Status.ShouldBe(MessageStatus.Sent);
        await _runHold.Received(1).DisposeAsync();
    }

    [Test]
    public async Task ARunInProgress_KeepsASecondRunFromSendingOrRecordingAnything()
    {
        _runLock.TryAcquireAsync(Arg.Any<CancellationToken>()).Returns((IAsyncDisposable?)null);

        var refused = await Should.ThrowAsync<InvalidOperationException>(() =>
            _sut.SendBroadcastToRecipientsAsync(Set(0, Accepted), Text, MessagingConstants.DefaultContentType));

        refused.Message.ShouldBe(MessagingConstants.BroadcastAlreadyRunningError);
        _handler.Bodies.ShouldBeEmpty();
        _stored.ShouldBeEmpty();
    }

    [Test]
    public async Task TheHttpRoute_RunsUnderTheSameLock()
    {
        _runLock.TryAcquireAsync(Arg.Any<CancellationToken>()).Returns((IAsyncDisposable?)null);
        var client = Guid.NewGuid();
        _contacts.GetFirstContactsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, MessengerContact>
            {
                [client] = new() { ClientId = client, Type = MessengerType.Telegram, Value = Accepted }
            });

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.SendBroadcastToClientsAsync(
            _provider.Name, new[] { client }, Text, MessagingConstants.DefaultContentType, MessagingConstants.BroadcastGroupEmptyError));

        _handler.Bodies.ShouldBeEmpty();
    }

    [Test]
    public async Task TheRunLock_IsReleasedWhenTheRunEnds()
    {
        await _sut.SendBroadcastToRecipientsAsync(Set(0, Accepted), Text, MessagingConstants.DefaultContentType);

        await _runHold.Received(1).DisposeAsync();
    }

    [Test]
    public async Task TheGivenAddresses_AreUsedWithoutResolvingAgain()
    {
        await _sut.SendBroadcastToRecipientsAsync(Set(0, Accepted), Text, MessagingConstants.DefaultContentType);

        await _contacts.DidNotReceiveWithAnyArgs().GetFirstContactsAsync(default!, default, default);
    }

    [Test]
    public async Task ADisabledProvider_SendsNothing()
    {
        _provider.IsEnabled = false;

        await Should.ThrowAsync<InvalidOperationException>(() =>
            _sut.SendBroadcastToRecipientsAsync(Set(0, Accepted), Text, MessagingConstants.DefaultContentType));

        _handler.Bodies.ShouldBeEmpty();
    }

    [Test]
    public async Task Resolving_PrefersTheContact_CountsTheRestAsSkipped_AndSendsNothing()
    {
        var withContact = Guid.NewGuid();
        var withNothing = Guid.NewGuid();
        _contacts.GetFirstContactsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, MessengerContact>
            {
                [withContact] = new() { ClientId = withContact, Type = MessengerType.Telegram, Value = Accepted }
            });

        var set = await _sut.ResolveBroadcastRecipientsAsync(_provider.Name, new[] { withContact, withNothing });

        set.ProviderId.ShouldBe(_provider.Id);
        set.Recipients.ShouldHaveSingleItem().Recipient.ShouldBe(Accepted);
        set.Skipped.ShouldBe(1);
        _handler.Bodies.ShouldBeEmpty();
    }

    [Test]
    public async Task TheBroadcastToClients_UsesTheSameAddressRule()
    {
        var withContact = Guid.NewGuid();
        _contacts.GetFirstContactsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, MessengerContact>
            {
                [withContact] = new() { ClientId = withContact, Type = MessengerType.Telegram, Value = Accepted }
            });

        var result = await _sut.SendBroadcastToClientsAsync(
            _provider.Name, new[] { withContact, Guid.NewGuid() }, Text, MessagingConstants.DefaultContentType, MessagingConstants.BroadcastGroupEmptyError);

        result.Sent.ShouldBe(1);
        result.SkippedNoContact.ShouldBe(1);
    }
}
