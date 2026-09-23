// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MessagingPluginClientReplyChannel, the adapter that lets the clarification dialog
/// reach an employee's stored personal messenger contact: resolves the contact of the same messenger
/// type, only returns it when the enabled provider's adapter classifies it as a personal address (a
/// provider without that capability counts as "no"), matches the channel name to the provider type
/// case-insensitively (Line vs LINE), sends through MessagingService as "Klacksy", maps every failure
/// to a result value, and keeps its constructor free of kernel services (DI leaf).
/// </summary>

using Klacks.Api.Infrastructure.Plugins;
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Plugins;

[TestFixture]
public class MessagingPluginClientReplyChannelTests
{
    private static readonly Guid ClientId = Guid.NewGuid();

    private IPluginStateChecker _pluginStateChecker = null!;
    private IMessengerContactRepository _contactRepository = null!;
    private IMessagingProviderRepository _providerRepository = null!;
    private IMessagingProviderAdapterFactory _adapterFactory = null!;
    private IMessagingService _messagingService = null!;
    private IMessagingProviderAdapter _classifyingAdapter = null!;
    private MessagingPluginClientReplyChannel _channel = null!;

    [SetUp]
    public void SetUp()
    {
        _pluginStateChecker = Substitute.For<IPluginStateChecker>();
        _pluginStateChecker.IsEnabled(MessagingConstants.PluginName).Returns(true);
        _contactRepository = Substitute.For<IMessengerContactRepository>();
        _providerRepository = Substitute.For<IMessagingProviderRepository>();
        _adapterFactory = Substitute.For<IMessagingProviderAdapterFactory>();
        _messagingService = Substitute.For<IMessagingService>();

        _classifyingAdapter = Substitute.For<IMessagingProviderAdapter, IPersonalRecipientClassifier>();
        ((IPersonalRecipientClassifier)_classifyingAdapter).IsPersonalRecipient("123456789").Returns(true);
        ((IPersonalRecipientClassifier)_classifyingAdapter).IsPersonalRecipient("-100200").Returns(false);

        _providerRepository.GetEnabledAsync().Returns(new List<MessagingProvider>
        {
            new() { Id = Guid.NewGuid(), Name = "telegram", ProviderType = "Telegram", IsEnabled = true },
            new() { Id = Guid.NewGuid(), Name = "line", ProviderType = "LINE", IsEnabled = true }
        });
        _adapterFactory.Create("Telegram").Returns(_classifyingAdapter);
        _adapterFactory.Create("LINE").Returns(_classifyingAdapter);

        _channel = new MessagingPluginClientReplyChannel(
            _pluginStateChecker, _contactRepository, _providerRepository, _adapterFactory, _messagingService,
            Substitute.For<ILogger<MessagingPluginClientReplyChannel>>());
    }

    private void ContactIs(MessengerType type, string value) =>
        _contactRepository.GetByClientAndTypeAsync(ClientId, type, Arg.Any<CancellationToken>())
            .Returns(new MessengerContact { Id = Guid.NewGuid(), ClientId = ClientId, Type = type, Value = value });

    [Test]
    public void Constructor_TakesOnlyPluginLevelServices_SoItStaysADependencyLeaf()
    {
        var parameterTypes = typeof(MessagingPluginClientReplyChannel).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToList();

        Assert.That(parameterTypes, Is.EquivalentTo(new[]
        {
            typeof(IPluginStateChecker),
            typeof(IMessengerContactRepository),
            typeof(IMessagingProviderRepository),
            typeof(IMessagingProviderAdapterFactory),
            typeof(IMessagingService),
            typeof(ILogger<MessagingPluginClientReplyChannel>)
        }));
    }

    [Test]
    public async Task PersonalContact_IsReturned()
    {
        ContactIs(MessengerType.Telegram, "123456789");

        (await _channel.ResolvePersonalRecipientAsync(ClientId, "Telegram")).ShouldBe("123456789");
    }

    [Test]
    public async Task GroupContact_IsNotReturned()
    {
        ContactIs(MessengerType.Telegram, "-100200");

        (await _channel.ResolvePersonalRecipientAsync(ClientId, "Telegram")).ShouldBeNull();
    }

    [Test]
    public async Task AdapterWithoutTheCapability_CountsAsNotPersonal()
    {
        ContactIs(MessengerType.Telegram, "123456789");
        _adapterFactory.Create("Telegram").Returns(Substitute.For<IMessagingProviderAdapter>());

        (await _channel.ResolvePersonalRecipientAsync(ClientId, "Telegram")).ShouldBeNull();
    }

    [Test]
    public async Task ChannelName_MatchesTheProviderTypeCaseInsensitively()
    {
        ContactIs(MessengerType.Line, "123456789");

        (await _channel.ResolvePersonalRecipientAsync(ClientId, "Line")).ShouldBe("123456789");
    }

    [Test]
    public async Task NoContact_ReturnsNull()
    {
        _contactRepository.GetByClientAndTypeAsync(ClientId, MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns((MessengerContact?)null);

        (await _channel.ResolvePersonalRecipientAsync(ClientId, "Telegram")).ShouldBeNull();
    }

    [Test]
    public async Task ProviderNotEnabled_ReturnsNull()
    {
        ContactIs(MessengerType.Telegram, "123456789");
        _providerRepository.GetEnabledAsync().Returns(new List<MessagingProvider>());

        (await _channel.ResolvePersonalRecipientAsync(ClientId, "Telegram")).ShouldBeNull();
    }

    [Test]
    public async Task UnknownChannel_ReturnsNull()
    {
        (await _channel.ResolvePersonalRecipientAsync(ClientId, "CarrierPigeon")).ShouldBeNull();
    }

    [Test]
    public async Task PluginDisabled_ReturnsNullAndSendFails()
    {
        _pluginStateChecker.IsEnabled(MessagingConstants.PluginName).Returns(false);

        (await _channel.ResolvePersonalRecipientAsync(ClientId, "Telegram")).ShouldBeNull();
        (await _channel.SendAsync("Telegram", "123456789", "Frage?")).Success.ShouldBeFalse();
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task ContactLookupThrows_ReturnsNull()
    {
        _contactRepository.GetByClientAndTypeAsync(ClientId, MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns<MessengerContact?>(_ => throw new InvalidOperationException("db down"));

        (await _channel.ResolvePersonalRecipientAsync(ClientId, "Telegram")).ShouldBeNull();
    }

    [Test]
    public async Task Send_GoesThroughMessagingServiceAsKlacksy()
    {
        _messagingService.SendMessageAsync("Telegram", Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SendMessageResult(true, "42"));

        var result = await _channel.SendAsync("Telegram", "123456789", "Heißt das, du kannst heute nicht arbeiten?");

        result.Success.ShouldBeTrue();
        await _messagingService.Received(1).SendMessageAsync(
            "Telegram",
            Arg.Is<SendMessageRequest>(r => r.Recipient == "123456789"
                                            && r.Content == "Heißt das, du kannst heute nicht arbeiten?"
                                            && r.SenderDisplayName == MessagingConstants.KlacksySenderDisplayName),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Send_ProviderError_IsAFailedResult()
    {
        _messagingService.SendMessageAsync("Telegram", Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SendMessageResult(false, ErrorMessage: "chat not found"));

        var result = await _channel.SendAsync("Telegram", "123456789", "Frage?");

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe("chat not found");
    }

    [Test]
    public async Task Send_Throws_IsAFailedResult()
    {
        _messagingService.SendMessageAsync("Telegram", Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns<SendMessageResult>(_ => throw new HttpRequestException("timeout"));

        var result = await _channel.SendAsync("Telegram", "123456789", "Frage?");

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe("timeout");
    }
}
