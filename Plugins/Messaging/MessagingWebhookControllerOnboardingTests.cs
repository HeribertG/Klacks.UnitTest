// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards that the Telegram onboarding path stays outside the inbound sender check (E61).
/// An employee who is just linking their account is unknown by definition, so discarding unknown
/// senders would break onboarding if the /start command reached the persist path. It does not:
/// the controller intercepts and returns before ProcessIncomingMessageAsync is ever called, and
/// these tests pin exactly that ordering.
/// </summary>
using System.Text;
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Presentation.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class MessagingWebhookControllerOnboardingTests
{
    private const string OnboardingToken = "abc123";
    private const string ChatId = "999111";

    private static readonly string StartCommandPayload =
        "{\"message\":{\"message_id\":7,\"text\":\"/start " + OnboardingToken + "\",\"chat\":{\"id\":" + ChatId + "}}}";

    private static readonly string PlainMessagePayload =
        "{\"message\":{\"message_id\":8,\"text\":\"guten morgen\",\"chat\":{\"id\":" + ChatId + "}}}";

    private IMessagingService _messagingService = null!;
    private IMessagingProviderRepository _providerRepository = null!;
    private IPluginEventBus _eventBus = null!;
    private ITelegramOnboardingRedemptionService _redemptionService = null!;
    private IUserMessengerPairingService _pairingService = null!;
    private MessagingWebhookController _sut = null!;

    [SetUp]
    public void Setup()
    {
        _messagingService = Substitute.For<IMessagingService>();
        _providerRepository = Substitute.For<IMessagingProviderRepository>();
        _eventBus = Substitute.For<IPluginEventBus>();
        _redemptionService = Substitute.For<ITelegramOnboardingRedemptionService>();
        _pairingService = Substitute.For<IUserMessengerPairingService>();

        _redemptionService
            .RedeemAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(OnboardingRedeemResult.Success);

        _pairingService
            .RedeemAsync(
                Arg.Any<string>(),
                Arg.Any<MessengerType>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(OnboardingRedeemResult.Success);

        _messagingService
            .ProcessIncomingMessageAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(new WebhookProcessingResult());

        _sut = new MessagingWebhookController(
            _messagingService,
            _providerRepository,
            _eventBus,
            _redemptionService,
            _pairingService,
            NullLogger<MessagingWebhookController>.Instance);
    }

    [Test]
    public async Task ReceiveWebhook_StartCommand_IsRedeemedAndNeverReachesThePersistPath()
    {
        GiveRequestBody(StartCommandPayload);

        var result = await _sut.ReceiveWebhook(MessagingConstants.ProviderTelegram);

        result.ShouldBeOfType<OkResult>();
        await _redemptionService.Received(1).RedeemAsync(OnboardingToken, ChatId, Arg.Any<CancellationToken>());
        await _messagingService.DidNotReceive().ProcessIncomingMessageAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReceiveWebhook_PlainMessage_StillReachesThePersistPath()
    {
        GiveRequestBody(PlainMessagePayload);

        await _sut.ReceiveWebhook(MessagingConstants.ProviderTelegram);

        await _redemptionService.DidNotReceive().RedeemAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _messagingService.Received(1).ProcessIncomingMessageAsync(
            MessagingConstants.ProviderTelegram,
            PlainMessagePayload,
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A discarded sender must look exactly like an accepted one from the outside. Anything else
    /// turns the webhook into an oracle for probing which chat ids the installation knows.
    /// </summary>
    [Test]
    public async Task ReceiveWebhook_DiscardedMessage_StillAnswersOk()
    {
        GiveRequestBody(PlainMessagePayload);

        var result = await _sut.ReceiveWebhook(MessagingConstants.ProviderTelegram);

        result.ShouldBeOfType<OkResult>();
        await _eventBus.DidNotReceive().BroadcastAsync(Arg.Any<string>(), Arg.Any<object>());
    }

    /// <summary>
    /// The regression guard for the employee path. One /start command now carries two kinds of code,
    /// and the employee invitation stays first: a token it accepts must never also be offered to the
    /// pairing store, or a valid invitation could be answered twice.
    /// </summary>
    [Test]
    public async Task ReceiveWebhook_EmployeeToken_IsNotAlsoOfferedToThePairingService()
    {
        GiveRequestBody(StartCommandPayload);

        await _sut.ReceiveWebhook(MessagingConstants.ProviderTelegram);

        await _redemptionService.Received(1).RedeemAsync(OnboardingToken, ChatId, Arg.Any<CancellationToken>());
        await _pairingService.DidNotReceive().RedeemAsync(
            Arg.Any<string>(),
            Arg.Any<MessengerType>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReceiveWebhook_CodeNoInvitationKnows_IsOfferedToThePairingService()
    {
        GiveRequestBody(StartCommandPayload);
        _redemptionService
            .RedeemAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(OnboardingRedeemResult.TokenNotFound);

        var result = await _sut.ReceiveWebhook(MessagingConstants.ProviderTelegram);

        result.ShouldBeOfType<OkResult>();
        await _pairingService.Received(1).RedeemAsync(
            OnboardingToken,
            MessengerType.Telegram,
            ChatId,
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A code neither redeemer knows still ends here. Letting it fall through to the persist path
    /// would newly expose it to the unknown-sender discard and change a route that works today.
    /// </summary>
    [Test]
    public async Task ReceiveWebhook_CodeNobodyKnows_StillNeverReachesThePersistPath()
    {
        GiveRequestBody(StartCommandPayload);
        _redemptionService
            .RedeemAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(OnboardingRedeemResult.TokenNotFound);
        _pairingService
            .RedeemAsync(
                Arg.Any<string>(),
                Arg.Any<MessengerType>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(OnboardingRedeemResult.TokenNotFound);

        var result = await _sut.ReceiveWebhook(MessagingConstants.ProviderTelegram);

        result.ShouldBeOfType<OkResult>();
        await _messagingService.DidNotReceive().ProcessIncomingMessageAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReceiveWebhook_PlainMessage_IsOfferedToNeitherRedeemer()
    {
        GiveRequestBody(PlainMessagePayload);

        await _sut.ReceiveWebhook(MessagingConstants.ProviderTelegram);

        await _pairingService.DidNotReceive().RedeemAsync(
            Arg.Any<string>(),
            Arg.Any<MessengerType>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    private void GiveRequestBody(string body)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

        _sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }
}
