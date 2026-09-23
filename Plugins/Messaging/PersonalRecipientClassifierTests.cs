// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests the per-provider personal-address check the clarification dialog relies on before it sends a
/// private question: Slack user ids (U/W) vs channel, group and DM-channel ids, Telegram positive chat
/// ids vs negative group ids, LINE user ids (U) vs group (C) and room (R) ids, WhatsApp/Viber always
/// personal, and that send-only providers do not claim the capability at all.
/// </summary>

using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Infrastructure.Services.Providers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class PersonalRecipientClassifierTests
{
    private HttpClient _httpClient = null!;
    private MemoryCache _cache = null!;

    [SetUp]
    public void SetUp()
    {
        _httpClient = new HttpClient();
        _cache = new MemoryCache(new MemoryCacheOptions());
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient.Dispose();
        _cache.Dispose();
    }

    [TestCase("U0BLRED0TK2", true)]
    [TestCase("W012A3CDE", true)]
    [TestCase(" U0BLRED0TK2 ", true)]
    [TestCase("C024BE91L", false)]
    [TestCase("G024BE91L", false)]
    [TestCase("D024BE91L", false)]
    [TestCase("#general", false)]
    [TestCase("U", false)]
    [TestCase("", false)]
    public void Slack(string recipient, bool expected)
    {
        new SlackMessagingProvider(_httpClient, Substitute.For<ILogger<SlackMessagingProvider>>())
            .IsPersonalRecipient(recipient).ShouldBe(expected);
    }

    [TestCase("123456789", true)]
    [TestCase("-1001234567890", false)]
    [TestCase("-42", false)]
    [TestCase("0", false)]
    [TestCase("@anna", false)]
    [TestCase("", false)]
    public void Telegram(string recipient, bool expected)
    {
        new TelegramMessagingProvider(_httpClient, _cache, Substitute.For<ILogger<TelegramMessagingProvider>>())
            .IsPersonalRecipient(recipient).ShouldBe(expected);
    }

    [TestCase("U4af4980629a1b2c3d4e5f6a7b8c9d0e1", true)]
    [TestCase("Ca56f94637cc4347f90a25382909b24b9", false)]
    [TestCase("Rb1d2c3e4f5a6b7c8d9e0f1a2b3c4d5e6", false)]
    [TestCase("", false)]
    public void Line(string recipient, bool expected)
    {
        new LineMessagingProvider(_httpClient, Substitute.For<ILogger<LineMessagingProvider>>())
            .IsPersonalRecipient(recipient).ShouldBe(expected);
    }

    [TestCase("41791234567", true)]
    [TestCase(" ", false)]
    public void WhatsApp(string recipient, bool expected)
    {
        new WhatsAppMessagingProvider(_httpClient, Substitute.For<ILogger<WhatsAppMessagingProvider>>())
            .IsPersonalRecipient(recipient).ShouldBe(expected);
    }

    [TestCase("01234567890A=", true)]
    [TestCase("", false)]
    public void Viber(string recipient, bool expected)
    {
        new ViberMessagingProvider(_httpClient, Substitute.For<ILogger<ViberMessagingProvider>>())
            .IsPersonalRecipient(recipient).ShouldBe(expected);
    }

    [TestCase(typeof(SlackMessagingProvider), true)]
    [TestCase(typeof(TelegramMessagingProvider), true)]
    [TestCase(typeof(LineMessagingProvider), true)]
    [TestCase(typeof(WhatsAppMessagingProvider), true)]
    [TestCase(typeof(ViberMessagingProvider), true)]
    [TestCase(typeof(SignalMessagingProvider), false)]
    [TestCase(typeof(SmsMessagingProvider), false)]
    [TestCase(typeof(TeamsMessagingProvider), false)]
    [TestCase(typeof(ThreemaMessagingProvider), false)]
    [TestCase(typeof(WeChatMessagingProvider), false)]
    [TestCase(typeof(ZaloMessagingProvider), false)]
    [TestCase(typeof(KakaoTalkMessagingProvider), false)]
    public void OnlyReceivingProviders_ClaimTheCapability(Type providerType, bool expected)
    {
        typeof(IPersonalRecipientClassifier).IsAssignableFrom(providerType).ShouldBe(expected);
    }
}
