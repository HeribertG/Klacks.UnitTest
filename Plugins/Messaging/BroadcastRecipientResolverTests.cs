// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The broadcast address rule on its own: the contact wins, the mobile number helps only where the provider accepts one, a blank
/// contact falls back, everybody else is skipped, an unknown provider type is refused, and phone numbers are not read needlessly.
/// </summary>

using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging.Application.Services;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Domain.Models;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class BroadcastRecipientResolverTests
{
    private const string Whatsapp = "WhatsApp";
    private const string Chat = "chat-1";
    private const string Mobile = "+41790000000";

    private static readonly Guid Client = Guid.NewGuid();

    private IMessengerContactRepository _contacts = null!;
    private IClientPhoneReader _phones = null!;
    private BroadcastRecipientResolver _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _contacts = Substitute.For<IMessengerContactRepository>();
        _contacts.GetFirstContactsAsync(default!, default, default).ReturnsForAnyArgs(new Dictionary<Guid, MessengerContact>());
        _phones = Substitute.For<IClientPhoneReader>();
        _phones.GetMobilePhonesAsync(default!, default).ReturnsForAnyArgs(new Dictionary<Guid, string?> { [Client] = Mobile });
        _sut = new BroadcastRecipientResolver(_contacts, _phones);
    }

    private void GiveContact(string value) =>
        _contacts.GetFirstContactsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), MessengerType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, MessengerContact> { [Client] = new() { ClientId = Client, Type = MessengerType.WhatsApp, Value = value } });

    [Test]
    public async Task TheContact_WinsOverTheMobileNumber()
    {
        GiveContact(Chat);

        var (recipients, _) = await _sut.ResolveAsync(Whatsapp, true, new[] { Client });

        recipients.ShouldHaveSingleItem().ShouldBe(new BroadcastRecipient(Client, Chat, null, FromPhoneFallback: false));
    }

    [Test]
    public async Task ABlankContact_FallsBackToTheMobileNumber()
    {
        GiveContact(" ");

        var (recipients, _) = await _sut.ResolveAsync(Whatsapp, true, new[] { Client });

        recipients.ShouldHaveSingleItem().ShouldBe(new BroadcastRecipient(Client, Mobile, null, FromPhoneFallback: true));
    }

    [Test]
    public async Task WithoutPhoneSupport_TheClientIsSkipped_AndNoNumberIsRead()
    {
        var (recipients, skipped) = await _sut.ResolveAsync(Whatsapp, false, new[] { Client });

        recipients.ShouldBeEmpty();
        skipped.ShouldBe(1);
        await _phones.DidNotReceiveWithAnyArgs().GetMobilePhonesAsync(default!, default);
    }

    [Test]
    public async Task AnUnknownProviderType_IsRefused() =>
        await Should.ThrowAsync<InvalidOperationException>(() => _sut.ResolveAsync("Carrier-Pigeon", false, new[] { Client }));
}
