// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The deterministic reach advice: reachable per channel, no messenger, send-only channels, a switched-off provider and an empty
/// audience, which never says why it is empty and carries no count of hidden people.
/// </summary>

using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.Services;
using Klacks.Plugin.Messaging.Domain.Models.Reach;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class MessagingReachAdvisorTests
{
    private const string Telegram = "Telegram";
    private const string Sms = "SMS";
    private const string Whatsapp = "WhatsApp";

    private static readonly Guid Anna = Guid.NewGuid();
    private static readonly Guid Bruno = Guid.NewGuid();
    private static readonly Guid Carla = Guid.NewGuid();
    private static readonly Guid Dora = Guid.NewGuid();
    private static readonly Guid[] Audience = [Anna, Bruno, Carla, Dora];

    private static ChannelReach Channel(string provider, bool enabled, bool reply, Guid[] viaContact, Guid[]? viaPhone = null) =>
        new(provider, enabled, reply, viaContact.ToHashSet(), (viaPhone ?? []).ToHashSet());

    private static MessagingReachAdvice Evaluate(params ChannelReach[] channels) =>
        MessagingReachAdvisor.Evaluate(new MessagingReachFacts(Audience, channels), []);

    private static int CountOf(MessagingReachAdvice advice, string code, string? provider = null) =>
        advice.Findings.Single(finding => finding.Code == code && finding.Provider == provider).Count;

    [Test]
    public void EveryEnabledChannel_ReportsWhomItReaches()
    {
        var advice = Evaluate(
            Channel(Telegram, enabled: true, reply: true, [Anna, Bruno]),
            Channel(Sms, enabled: true, reply: false, [], [Bruno, Carla]));

        CountOf(advice, MessagingReachFindingCodes.ReachableViaChannel, Telegram).ShouldBe(2);
        CountOf(advice, MessagingReachFindingCodes.ReachableViaChannel, Sms).ShouldBe(2);
        advice.ReachableCount.ShouldBe(3);
        advice.Channels.Single(channel => channel.Provider == Sms).ViaPhone.ShouldBe(2);
    }

    [Test]
    public void PeopleWithoutAnyAddress_AreNoMessenger_AndMissing()
    {
        var advice = Evaluate(Channel(Telegram, enabled: true, reply: true, [Anna]));

        CountOf(advice, MessagingReachFindingCodes.NoMessenger).ShouldBe(3);
        advice.MissingCount.ShouldBe(3);
        MessagingReachAdvisor.MissingClientIds(new MessagingReachFacts(Audience, [Channel(Telegram, true, true, [Anna])]))
            .ShouldBe(new[] { Bruno, Carla, Dora });
    }

    [Test]
    public void PeopleOnlyOnASendOnlyChannel_AreFound_EvenWhenAnotherChannelCouldReachOthers()
    {
        var advice = Evaluate(
            Channel(Telegram, enabled: true, reply: true, [Anna]),
            Channel(Sms, enabled: true, reply: false, [], [Anna, Bruno]));

        CountOf(advice, MessagingReachFindingCodes.SendOnlyNoReply).ShouldBe(1);
        advice.SendOnlyCount.ShouldBe(1);
        advice.ReachableWithReplyCount.ShouldBe(1);
    }

    [Test]
    public void ASwitchedOffProvider_CountsOnlyWhomItWouldAddToTheReach()
    {
        var advice = Evaluate(
            Channel(Telegram, enabled: true, reply: true, [Anna]),
            Channel(Whatsapp, enabled: false, reply: true, [Anna, Bruno]));

        CountOf(advice, MessagingReachFindingCodes.ProviderDisabled, Whatsapp).ShouldBe(1);
        CountOf(advice, MessagingReachFindingCodes.NoMessenger).ShouldBe(2);
        advice.ReachableCount.ShouldBe(1);
    }

    [Test]
    public void ASwitchedOffProvider_IsNotCountedAsReach()
    {
        var advice = Evaluate(Channel(Whatsapp, enabled: false, reply: true, [Anna]));

        advice.ReachableCount.ShouldBe(0);
        advice.BestChannel.ShouldBeNull();
        advice.Findings.ShouldContain(finding => finding.Code == MessagingReachFindingCodes.NoEnabledChannel);
    }

    [Test]
    public void AnEmptyAudience_HasOneFindingWithoutACount()
    {
        var advice = MessagingReachAdvisor.Evaluate(new MessagingReachFacts([], []), []);

        advice.Findings.ShouldHaveSingleItem().ShouldBe(new MessagingReachFinding(MessagingReachFindingCodes.NoVisibleClients, 0));
        advice.AudienceCount.ShouldBe(0);
    }

    [Test]
    public void TheBestChannel_ReachesMost_AndATieGoesToTheTwoWayChannel()
    {
        var advice = Evaluate(
            Channel(Sms, enabled: true, reply: false, [], [Anna, Bruno]),
            Channel(Telegram, enabled: true, reply: true, [Bruno, Carla]));

        advice.BestChannel.ShouldBe(Telegram);
    }

    [Test]
    public void TheRecommendation_NamesTheBestChannel_TheSecondSend_AndTheMissing()
    {
        var advice = Evaluate(
            Channel(Telegram, enabled: true, reply: true, [Anna, Bruno]),
            Channel(Sms, enabled: true, reply: false, [], [Carla]));

        advice.Recommendation.ShouldBe(
            "Telegram reaches 2 of 4 people. One send uses one channel, so the other 1 reachable people need a second send on another channel." +
            " 1 people cannot be reached on any enabled channel.");
    }

    [Test]
    public void TheRecommendation_WarnsWhenTheBestChannelIsSendOnly()
    {
        var advice = Evaluate(Channel(Sms, enabled: true, reply: false, [], Audience));

        advice.Recommendation.ShouldBe("SMS reaches 4 of 4 people. SMS is send-only: recipients cannot answer there.");
    }

    [Test]
    public void TheSameFacts_GiveTheSameAdvice()
    {
        ChannelReach[] channels = [Channel(Telegram, true, true, [Anna]), Channel(Sms, true, false, [], [Bruno])];
        var first = Evaluate(channels);
        var second = Evaluate(channels.Reverse().ToArray());

        second.Channels.ShouldBe(first.Channels);
        second.Findings.ShouldBe(first.Findings);
        second.Recommendation.ShouldBe(first.Recommendation);
    }
}
