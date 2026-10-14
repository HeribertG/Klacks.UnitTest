// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The facts a send_message confirmation is bound to: the provider, the recipient address, the content type and a hash of
/// the text (never the text itself). The items are few enough to be stored whole (the host drops the items of a binding that
/// exceeds its storage cap, and the plugin compares the items, not a fingerprint), and a binding matches only when kind and
/// all four items are equal, whatever the order the host stored them in.
/// </summary>

using System.Security.Cryptography;
using System.Text;
using Klacks.Plugin.Contracts.Skills;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.Services;
using Klacks.Plugin.Messaging.Domain.Models;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class SendMessageBindingTests
{
    private const int HostStorageCapOfBindingItems = 50;

    private static MessageRecipientResolution Resolved(string provider = "telegram", string identifier = "chat-123") =>
        MessageRecipientResolution.Resolved(provider, "Muster, Hans", identifier);

    [Test]
    public void TheItems_BindProviderRecipientContentTypeAndTheHashOfTheText_NotTheText()
    {
        var items = SendMessageBinding.ItemsFor(Resolved(), "Hallo Welt", "text");

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Hallo Welt"))).ToLowerInvariant();
        items.OrderBy(item => item, StringComparer.Ordinal).ShouldBe(new[]
        {
            "content-sha256:" + hash,
            "content-type:text",
            "provider:telegram",
            "recipient:chat-123"
        });
        items.ShouldNotContain(item => item.Contains("Hallo Welt", StringComparison.Ordinal));
    }

    [Test]
    public void TheItems_AreFarBelowTheHostStorageCap()
    {
        SendMessageBinding.ItemsFor(Resolved(), new string('x', 100_000), "text").Count.ShouldBeLessThan(HostStorageCapOfBindingItems);
    }

    [Test]
    public void ABindingMatches_WhateverTheOrderOfTheStoredItems()
    {
        var current = SendMessageBinding.ItemsFor(Resolved(), "Hallo", "text");
        var confirmed = new PluginConfirmedBinding(MessagingConstants.MessageBindingKind, current.Reverse().ToList());

        SendMessageBinding.Matches(confirmed, current).ShouldBeTrue();
    }

    [TestCase("provider", "whatsapp", "chat-123", "Hallo", "text")]
    [TestCase("recipient", "telegram", "chat-999", "Hallo", "text")]
    [TestCase("text", "telegram", "chat-123", "Hallo!", "text")]
    [TestCase("content type", "telegram", "chat-123", "Hallo", "document")]
    public void ABindingDoesNotMatch_WhenOneFactDiffers(string fact, string provider, string identifier, string content, string contentType)
    {
        var confirmed = new PluginConfirmedBinding(
            MessagingConstants.MessageBindingKind, SendMessageBinding.ItemsFor(Resolved(), "Hallo", "text"));

        SendMessageBinding.Matches(confirmed, SendMessageBinding.ItemsFor(Resolved(provider, identifier), content, contentType))
            .ShouldBeFalse($"a different {fact} must not match");
    }

    [Test]
    public void ABindingOfAnotherKind_OrWithNoItems_NeverMatches()
    {
        var current = SendMessageBinding.ItemsFor(Resolved(), "Hallo", "text");

        SendMessageBinding.Matches(new PluginConfirmedBinding("work", current), current).ShouldBeFalse();
        SendMessageBinding.Matches(new PluginConfirmedBinding(MessagingConstants.MessageBindingKind, []), current).ShouldBeFalse();
    }
}
