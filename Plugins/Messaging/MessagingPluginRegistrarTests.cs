// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests that every messaging provider can actually be resolved from the plugin's DI
/// registration. Each provider is a typed HttpClient whose handler pipeline is only built on
/// resolution, so a missing handler registration would otherwise surface at the first send.
/// </summary>
using Klacks.Plugin.Messaging;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Infrastructure.Services.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class MessagingPluginRegistrarTests
{
    private ServiceProvider _serviceProvider = null!;

    [SetUp]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        new MessagingPluginRegistrar().RegisterServices(services, new ConfigurationBuilder().Build());

        _serviceProvider = services.BuildServiceProvider();
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider.Dispose();
    }

    [TestCase(typeof(TelegramMessagingProvider))]
    [TestCase(typeof(WhatsAppMessagingProvider))]
    [TestCase(typeof(SignalMessagingProvider))]
    [TestCase(typeof(SmsMessagingProvider))]
    [TestCase(typeof(ThreemaMessagingProvider))]
    [TestCase(typeof(ViberMessagingProvider))]
    [TestCase(typeof(LineMessagingProvider))]
    [TestCase(typeof(KakaoTalkMessagingProvider))]
    [TestCase(typeof(WeChatMessagingProvider))]
    [TestCase(typeof(ZaloMessagingProvider))]
    [TestCase(typeof(TeamsMessagingProvider))]
    [TestCase(typeof(SlackMessagingProvider))]
    public void RegisterServices_Resolves_Provider_With_Its_Handler_Pipeline(Type providerType)
    {
        using var scope = _serviceProvider.CreateScope();

        var provider = scope.ServiceProvider.GetRequiredService(providerType);

        provider.ShouldBeAssignableTo<IMessagingProviderAdapter>();
    }
}
