// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// End-to-end guard for the Telegram /start authentication (C-1): the controller and the real
/// MessagingService with the real Telegram adapter. The webhook route is always "telegram" while a real
/// provider carries its own name (e.g. "klacks-bot"), so the provider must be resolved exactly like the
/// normal message path does - by name, then by the first enabled provider of that type - and a disabled
/// provider must never redeem an onboarding or pairing code.
/// </summary>
using System.Text;
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Infrastructure.Services;
using Klacks.Plugin.Messaging.Presentation.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class MessagingWebhookControllerTelegramStartAuthTests
{
    private const string ProviderName = "klacks-bot";
    private const string OnboardingToken = "abc123";
    private const string ChatId = "999111";
    private const string TelegramSecretHeader = "X-Telegram-Bot-Api-Secret-Token";
    private const string WebhookSecret = "onboarding-secret";

    private static readonly string StartCommandPayload =
        "{\"message\":{\"message_id\":7,\"text\":\"/start " + OnboardingToken + "\",\"chat\":{\"id\":" + ChatId + "}}}";

    private IMessagingProviderRepository _providerRepository = null!;
    private IMessagingInboundActivityTracker _activityTracker = null!;
    private ITelegramOnboardingRedemptionService _redemptionService = null!;
    private IUserMessengerPairingService _pairingService = null!;
    private MemoryCache _logSuppressionCache = null!;
    private ServiceProvider _serviceProvider = null!;
    private IServiceScope _scope = null!;
    private MessagingProvider _provider = null!;
    private MessagingWebhookController _sut = null!;

    [SetUp]
    public void Setup()
    {
        _provider = new MessagingProvider
        {
            Id = Guid.NewGuid(),
            Name = ProviderName,
            DisplayName = "Klacks Bot",
            ProviderType = MessagingConstants.ProviderTelegram,
            IsEnabled = true,
            WebhookSecret = WebhookSecret
        };

        _providerRepository = Substitute.For<IMessagingProviderRepository>();
        _providerRepository.GetByNameAsync(Arg.Any<string>()).Returns((MessagingProvider?)null);
        _providerRepository.GetEnabledAsync().Returns(new[] { _provider });

        _activityTracker = Substitute.For<IMessagingInboundActivityTracker>();
        _redemptionService = Substitute.For<ITelegramOnboardingRedemptionService>();
        _redemptionService
            .RedeemAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(OnboardingRedeemResult.Success);
        _pairingService = Substitute.For<IUserMessengerPairingService>();

        _logSuppressionCache = new MemoryCache(new MemoryCacheOptions());
        var services = new ServiceCollection();
        services.AddLogging();
        new MessagingPluginRegistrar().RegisterServices(services, new ConfigurationBuilder().Build());
        _serviceProvider = services.BuildServiceProvider();
        _scope = _serviceProvider.CreateScope();

        _sut = BuildController();
    }

    [TearDown]
    public void TearDown()
    {
        _logSuppressionCache.Dispose();
        _scope.Dispose();
        _serviceProvider.Dispose();
    }

    [Test]
    public async Task ReceiveWebhook_StartCommand_ProviderNamedDifferentlyThanRoute_IsAuthenticatedAndRedeemed()
    {
        GiveRequestBody(StartCommandPayload, WebhookSecret);

        var result = await _sut.ReceiveWebhook(MessagingConstants.ProviderTelegram);

        result.ShouldBeOfType<OkResult>();
        await _redemptionService.Received(1).RedeemAsync(OnboardingToken, ChatId, Arg.Any<CancellationToken>());
        _activityTracker.Received(1).RecordWebhookHit(_provider.Id);
    }

    [Test]
    public async Task ReceiveWebhook_StartCommand_WrongSecret_ReturnsUnauthorizedAndNeverRedeems()
    {
        GiveRequestBody(StartCommandPayload, "wrong-secret");

        var result = await _sut.ReceiveWebhook(MessagingConstants.ProviderTelegram);

        result.ShouldBeOfType<UnauthorizedResult>();
        await _redemptionService.DidNotReceive().RedeemAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        _activityTracker.Received(1).RecordSignatureRejection(_provider.Id);
    }

    [Test]
    public async Task ReceiveWebhook_StartCommand_DisabledProvider_ReturnsUnauthorizedAndNeverRedeems()
    {
        _provider.Name = MessagingConstants.ProviderTelegram;
        _provider.IsEnabled = false;
        _providerRepository.GetByNameAsync(MessagingConstants.ProviderTelegram).Returns(_provider);
        _providerRepository.GetEnabledAsync().Returns(Array.Empty<MessagingProvider>());
        GiveRequestBody(StartCommandPayload, WebhookSecret);

        var result = await _sut.ReceiveWebhook(MessagingConstants.ProviderTelegram);

        result.ShouldBeOfType<UnauthorizedResult>();
        await _redemptionService.DidNotReceive().RedeemAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _pairingService.DidNotReceive().RedeemAsync(
            Arg.Any<string>(), Arg.Any<MessengerType>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        _activityTracker.DidNotReceive().RecordWebhookHit(Arg.Any<Guid>());
    }

    private MessagingWebhookController BuildController()
    {
        var messagingService = new MessagingService(
            _providerRepository,
            Substitute.For<IMessageRepository>(),
            Substitute.For<IMessengerContactRepository>(),
            Substitute.For<IOwnerMessengerReader>(),
            Substitute.For<IUserMessengerContactRepository>(),
            Substitute.For<IAppUserDirectoryReader>(),
            Array.Empty<IInboundMessengerObserver>(),
            Array.Empty<IInboundClientMessengerObserver>(),
            _logSuppressionCache,
            Substitute.For<IClientGroupReader>(),
            Substitute.For<IClientIdNumberReader>(),
            Substitute.For<IClientPhoneReader>(),
            Substitute.For<IPluginUnitOfWork>(),
            Substitute.For<IPluginSettingsReader>(),
            _scope.ServiceProvider.GetRequiredService<MessagingProviderAdapterFactory>(),
            _activityTracker,
            NullLogger<MessagingService>.Instance);

        return new MessagingWebhookController(
            messagingService,
            _providerRepository,
            Substitute.For<IPluginEventBus>(),
            _redemptionService,
            _pairingService,
            NullLogger<MessagingWebhookController>.Instance);
    }

    private void GiveRequestBody(string body, string telegramSecret)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        httpContext.Request.Headers[TelegramSecretHeader] = telegramSecret;

        _sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }
}
