// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the SupportsStructuredActions capability of every provider adapter (E65).
/// The flag promises a round trip: a fixed set of selectable actions reaches the recipient and the
/// chosen one comes back. That is only possible for the five adapters whose ParseWebhookPayload can
/// return an inbound message at all; the other seven can notify but never ask, so they must report
/// false. The completeness test makes a newly added adapter fail here until its flag is decided.
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
public class MessagingProviderStructuredActionsTests
{
    private static readonly Dictionary<Type, bool> ExpectedSupport = new()
    {
        [typeof(TelegramMessagingProvider)] = true,
        [typeof(SlackMessagingProvider)] = true,
        [typeof(LineMessagingProvider)] = true,
        [typeof(ViberMessagingProvider)] = true,
        [typeof(WhatsAppMessagingProvider)] = true,
        [typeof(KakaoTalkMessagingProvider)] = false,
        [typeof(SignalMessagingProvider)] = false,
        [typeof(SmsMessagingProvider)] = false,
        [typeof(TeamsMessagingProvider)] = false,
        [typeof(ThreemaMessagingProvider)] = false,
        [typeof(WeChatMessagingProvider)] = false,
        [typeof(ZaloMessagingProvider)] = false,
    };

    private static readonly string[] WebhookPayloadShapes =
    {
        "{}",
        "{\"message\":{\"message_id\":7,\"text\":\"hallo\",\"chat\":{\"id\":42}}}",
        "{\"type\":\"event_callback\",\"event\":{\"type\":\"message\",\"user\":\"U1\",\"text\":\"hallo\"}}",
        "{\"events\":[{\"type\":\"message\",\"message\":{\"type\":\"text\",\"text\":\"hallo\"}}]}",
        "not json at all",
    };

    private ServiceProvider _serviceProvider = null!;
    private IServiceScope _scope = null!;

    public static IEnumerable<TestCaseData> AllProviders()
    {
        foreach (var entry in ExpectedSupport)
        {
            yield return new TestCaseData(entry.Key, entry.Value)
                .SetName($"{entry.Key.Name} supports structured actions: {entry.Value}");
        }
    }

    public static IEnumerable<TestCaseData> SendOnlyProviders()
    {
        foreach (var entry in ExpectedSupport.Where(e => !e.Value))
        {
            yield return new TestCaseData(entry.Key).SetName($"{entry.Key.Name} cannot receive");
        }
    }

    [SetUp]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        new MessagingPluginRegistrar().RegisterServices(services, new ConfigurationBuilder().Build());

        _serviceProvider = services.BuildServiceProvider();
        _scope = _serviceProvider.CreateScope();
    }

    [TearDown]
    public void TearDown()
    {
        _scope.Dispose();
        _serviceProvider.Dispose();
    }

    [TestCaseSource(nameof(AllProviders))]
    public void SupportsStructuredActions_MatchesTheDecidedCapability(Type providerType, bool expected)
    {
        var adapter = Resolve(providerType);

        adapter.SupportsStructuredActions.ShouldBe(expected);
    }

    /// <summary>
    /// Verifies the premise the false entries rest on: these adapters return null from
    /// ParseWebhookPayload no matter what arrives, so an answer could never reach the system.
    /// </summary>
    [TestCaseSource(nameof(SendOnlyProviders))]
    public void SendOnlyProviders_NeverParseAnInboundMessage(Type providerType)
    {
        var adapter = Resolve(providerType);

        foreach (var payload in WebhookPayloadShapes)
        {
            adapter.ParseWebhookPayload(payload).ShouldBeNull(
                $"{providerType.Name} returned an inbound message for payload: {payload}");
        }
    }

    [Test]
    public void EveryAdapterInTheAssembly_HasADecidedCapability()
    {
        var adapters = typeof(MessagingPluginRegistrar).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IMessagingProviderAdapter).IsAssignableFrom(t))
            .OrderBy(t => t.Name)
            .ToList();

        adapters.ShouldNotBeEmpty();
        adapters.Count.ShouldBe(ExpectedSupport.Count);

        foreach (var adapter in adapters)
        {
            ExpectedSupport.ShouldContainKey(adapter);
        }
    }

    private IMessagingProviderAdapter Resolve(Type providerType)
    {
        return (IMessagingProviderAdapter)_scope.ServiceProvider.GetRequiredService(providerType);
    }
}
