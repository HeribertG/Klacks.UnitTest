// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the singleton, memory-only inbound activity tracker: timestamps, unknown-sender
/// dedup/cap/ordering and TTL expiry.
/// </summary>
using Klacks.Plugin.Messaging;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging.Setup;

[TestFixture]
public class MessagingInboundActivityTrackerTests
{
    private DateTime _now;
    private MessagingInboundActivityTracker _sut = null!;

    [SetUp]
    public void Setup()
    {
        _now = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        _sut = new MessagingInboundActivityTracker(() => _now);
    }

    [Test]
    public void RecordWebhookHit_SetsLastWebhookHitUtc()
    {
        var providerId = Guid.NewGuid();

        _sut.RecordWebhookHit(providerId);

        _sut.GetSnapshot(providerId).LastWebhookHitUtc.ShouldBe(_now);
    }

    [Test]
    public void RecordSignatureRejection_SetsLastSignatureRejectionUtc()
    {
        var providerId = Guid.NewGuid();

        _sut.RecordSignatureRejection(providerId);

        _sut.GetSnapshot(providerId).LastSignatureRejectionUtc.ShouldBe(_now);
    }

    [Test]
    public void GetSnapshot_UnknownProviderId_ReturnsEmptySnapshot()
    {
        var snapshot = _sut.GetSnapshot(Guid.NewGuid());

        snapshot.LastWebhookHitUtc.ShouldBeNull();
        snapshot.LastSignatureRejectionUtc.ShouldBeNull();
        snapshot.UnknownSenders.ShouldBeEmpty();
    }

    [Test]
    public void RecordUnknownSender_MoreThanFive_KeepsOnlyTheFiveNewest()
    {
        var providerId = Guid.NewGuid();

        for (var i = 0; i < 6; i++)
        {
            _sut.RecordUnknownSender(providerId, $"sender-{i}", $"Sender {i}");
            _now = _now.AddMinutes(1);
        }

        var senders = _sut.GetSnapshot(providerId).UnknownSenders;

        senders.Count.ShouldBe(MessagingSetupConstants.MaxUnknownSendersPerProvider);
        senders.Select(s => s.SenderId).ShouldNotContain("sender-0");
        senders.Select(s => s.SenderId).ShouldContain("sender-5");
    }

    [Test]
    public void RecordUnknownSender_ReturnsNewestFirst()
    {
        var providerId = Guid.NewGuid();

        _sut.RecordUnknownSender(providerId, "a", "A");
        _now = _now.AddMinutes(1);
        _sut.RecordUnknownSender(providerId, "b", "B");

        var senders = _sut.GetSnapshot(providerId).UnknownSenders;

        senders[0].SenderId.ShouldBe("b");
        senders[1].SenderId.ShouldBe("a");
    }

    [Test]
    public void RecordUnknownSender_SameSenderTwice_KeepsOnlyOneWithNewestTimestamp()
    {
        var providerId = Guid.NewGuid();

        _sut.RecordUnknownSender(providerId, "sender-1", "First Name");
        var firstSeenUtc = _now;
        _now = _now.AddMinutes(5);
        _sut.RecordUnknownSender(providerId, "sender-1", "Updated Name");

        var senders = _sut.GetSnapshot(providerId).UnknownSenders;

        senders.Count.ShouldBe(1);
        senders[0].DisplayName.ShouldBe("Updated Name");
        senders[0].SeenAtUtc.ShouldBe(_now);
        senders[0].SeenAtUtc.ShouldNotBe(firstSeenUtc);
    }

    [Test]
    public void GetSnapshot_SightingOlderThanTtl_IsExcluded()
    {
        var providerId = Guid.NewGuid();

        _sut.RecordUnknownSender(providerId, "sender-1", "Stale Sender");
        _now = _now.AddHours(MessagingSetupConstants.UnknownSenderTtlHours).AddMinutes(1);

        _sut.GetSnapshot(providerId).UnknownSenders.ShouldBeEmpty();
    }

    /// <summary>
    /// A vendor-controlled display name (e.g. a Slack/Telegram profile name) must not be able to blow
    /// up the setup diagnosis payload; it is truncated the same way vendor-supplied error text is.
    /// </summary>
    [Test]
    public void RecordUnknownSender_LongDisplayName_IsTruncated()
    {
        var providerId = Guid.NewGuid();
        var longName = new string('a', MessagingSetupConstants.MaxVendorMessageLength + 50);

        _sut.RecordUnknownSender(providerId, "sender-1", longName);

        var stored = _sut.GetSnapshot(providerId).UnknownSenders.Single().DisplayName;
        stored!.Length.ShouldBe(MessagingSetupConstants.MaxVendorMessageLength);
    }

    [Test]
    public void RegisteredAsSingleton_SameInstanceAcrossScopes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        new MessagingPluginRegistrar().RegisterServices(services, new ConfigurationBuilder().Build());
        using var serviceProvider = services.BuildServiceProvider();

        using var scopeA = serviceProvider.CreateScope();
        using var scopeB = serviceProvider.CreateScope();

        var trackerA = scopeA.ServiceProvider.GetRequiredService<IMessagingInboundActivityTracker>();
        var trackerB = scopeB.ServiceProvider.GetRequiredService<IMessagingInboundActivityTracker>();

        trackerA.ShouldBeSameAs(trackerB);
    }
}
