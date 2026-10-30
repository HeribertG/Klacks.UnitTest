// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The facts a send_broadcast confirmation is bound to: a fixed handful of items whatever the audience size, no address and no text
/// in them, and any change of member, address, way of addressing, provider or text breaks the match. A binding of another kind
/// and an empty item list never match.
/// </summary>

using Klacks.Plugin.Contracts.Skills;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.Services;
using Klacks.Plugin.Messaging.Domain.Models;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class SendBroadcastBindingTests
{
    private const string Text = "Vertraulicher Text";
    private const string Address = "chat-4711";
    private const int LargeAudience = 500;
    private const int ItemCount = 5;

    private static readonly Guid ClientA = Guid.NewGuid();
    private static readonly Guid ClientB = Guid.NewGuid();

    private static BroadcastRecipientSet Set(params BroadcastRecipient[] recipients) =>
        new(Guid.NewGuid(), "Telegram", recipients, 0);

    private static BroadcastRecipient A(string address = Address, bool phone = false) => new(ClientA, address, null, phone);

    private static BroadcastRecipient B() => new(ClientB, "chat-2", null, false);

    private static PluginConfirmedBinding Confirmed(IReadOnlyList<string> items, string kind = MessagingConstants.BroadcastBindingKind) =>
        new(kind, items);

    [Test]
    public void TheItems_StayAFixedHandful_WhateverTheAudienceSize()
    {
        var recipients = Enumerable.Range(0, LargeAudience).Select(i => new BroadcastRecipient(Guid.NewGuid(), $"chat-{i}", null, false)).ToArray();

        SendBroadcastBinding.ItemsFor(Set(recipients), Text).Count.ShouldBe(ItemCount);
    }

    [Test]
    public void TheItems_CarryNoAddressAndNoText()
    {
        var items = string.Join(' ', SendBroadcastBinding.ItemsFor(Set(A()), Text));

        items.ShouldNotContain(Address);
        items.ShouldNotContain(Text);
    }

    [Test]
    public void TheSameFacts_Match_WhateverTheOrder()
    {
        var confirmed = Confirmed(SendBroadcastBinding.ItemsFor(Set(A(), B()), Text).Reverse().ToList());

        SendBroadcastBinding.Matches(confirmed, SendBroadcastBinding.ItemsFor(Set(B(), A()), Text)).ShouldBeTrue();
    }

    [Test]
    public void ADifferentText_DoesNotMatch() =>
        SendBroadcastBinding.Matches(
            Confirmed(SendBroadcastBinding.ItemsFor(Set(A()), Text)),
            SendBroadcastBinding.ItemsFor(Set(A()), Text + "!")).ShouldBeFalse();

    [Test]
    public void AnAddedMember_DoesNotMatch() =>
        SendBroadcastBinding.Matches(
            Confirmed(SendBroadcastBinding.ItemsFor(Set(A()), Text)),
            SendBroadcastBinding.ItemsFor(Set(A(), B()), Text)).ShouldBeFalse();

    [Test]
    public void AChangedAddress_DoesNotMatch() =>
        SendBroadcastBinding.Matches(
            Confirmed(SendBroadcastBinding.ItemsFor(Set(A()), Text)),
            SendBroadcastBinding.ItemsFor(Set(A("chat-other")), Text)).ShouldBeFalse();

    [Test]
    public void TheSameAddressFoundAnotherWay_DoesNotMatch() =>
        SendBroadcastBinding.Matches(
            Confirmed(SendBroadcastBinding.ItemsFor(Set(A()), Text)),
            SendBroadcastBinding.ItemsFor(Set(A(phone: true)), Text)).ShouldBeFalse();

    [Test]
    public void AnotherProvider_DoesNotMatch()
    {
        var other = Set(A()) with { ProviderName = "Slack" };

        SendBroadcastBinding.Matches(
            Confirmed(SendBroadcastBinding.ItemsFor(Set(A()), Text)),
            SendBroadcastBinding.ItemsFor(other, Text)).ShouldBeFalse();
    }

    [Test]
    public void TheBindingOfAnotherKind_DoesNotMatch()
    {
        var items = SendBroadcastBinding.ItemsFor(Set(A()), Text);

        SendBroadcastBinding.Matches(Confirmed(items, MessagingConstants.MessageBindingKind), items).ShouldBeFalse();
    }

    [Test]
    public void AnEmptyItemList_NeverMatches() =>
        SendBroadcastBinding.Matches(Confirmed([]), SendBroadcastBinding.ItemsFor(Set(A()), Text)).ShouldBeFalse();
}
