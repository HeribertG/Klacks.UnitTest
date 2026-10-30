// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The facts behind evaluate_messaging_reach with the REAL address rule, audience resolution and adapters' capabilities: the counts
/// equal what the rule resolves, a provider may be switched off, names come only for people no enabled channel reaches, the
/// serialized advice holds no address, a hidden group reads like an empty one without a count, and the skill relays the advice.
/// </summary>

using System.Text.Json;
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Application.Models;
using Klacks.Plugin.Messaging.Application.Services;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Domain.Models.Reach;
using Klacks.Plugin.Messaging.Skills;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class MessagingReachServiceTests
{
    private const string TelegramChat = "chat-777";
    private const string MobileNumber = "+41790000000";
    private const string MissingName = "Muster, Hans";

    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid WithContact = Guid.NewGuid();
    private static readonly Guid WithPhone = Guid.NewGuid();
    private static readonly Guid WithNothing = Guid.NewGuid();

    private IClientGroupReader _groupReader = null!;
    private IMessagingAccessScope _accessScope = null!;
    private IMessagingProviderRepository _providers = null!;
    private IMessagingProviderAdapterFactory _adapters = null!;
    private IMessengerContactRepository _contacts = null!;
    private IClientPhoneReader _phones = null!;
    private MessagingReachService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _groupReader = Substitute.For<IClientGroupReader>();
        _groupReader.GetClientIdsInGroupAsync(GroupId, Arg.Any<CancellationToken>()).Returns(new[] { WithContact, WithPhone, WithNothing });
        _accessScope = Substitute.For<IMessagingAccessScope>();
        _providers = Substitute.For<IMessagingProviderRepository>();
        _adapters = Substitute.For<IMessagingProviderAdapterFactory>();
        _contacts = Substitute.For<IMessengerContactRepository>();
        _phones = Substitute.For<IClientPhoneReader>();

        _contacts.GetFirstContactsAsync(default!, default, default).ReturnsForAnyArgs(new Dictionary<Guid, MessengerContact>());
        _contacts.GetFirstContactsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, MessengerContact>
            {
                [WithContact] = new() { ClientId = WithContact, Type = MessengerType.Telegram, Value = TelegramChat }
            });
        _phones.GetMobilePhonesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string?> { [WithPhone] = MobileNumber, [WithContact] = null });
        _contacts.GetClientDisplayNamesAsync(default!, default, default).ReturnsForAnyArgs(new[] { MissingName });

        GiveProviders(
            (MessagingConstants.ProviderTelegram, true, false),
            (MessagingConstants.ProviderSms, true, true));

        _sut = new MessagingReachService(
            new BroadcastAudienceResolver(_groupReader, Substitute.For<IClientIdNumberReader>(), _accessScope),
            _providers,
            _adapters,
            new BroadcastRecipientResolver(_contacts, _phones),
            _contacts);
    }

    private void GiveProviders(params (string Type, bool Enabled, bool Phone)[] providers)
    {
        _providers.GetAllAsync().Returns(providers
            .Select(p => new MessagingProvider { Name = p.Type, ProviderType = p.Type, IsEnabled = p.Enabled })
            .ToList());

        foreach (var provider in providers)
        {
            var adapter = Substitute.For<IMessagingProviderAdapter>();
            adapter.SupportsPhoneAsRecipient.Returns(provider.Phone);
            _adapters.Create(provider.Type).Returns(adapter);
        }
    }

    private static BroadcastAudienceParameters Group() => new(GroupId, []);

    [Test]
    public async Task TheCounts_FollowTheAddressRule()
    {
        var advice = await _sut.EvaluateAsync(Group(), isAdmin: true);

        advice.AudienceCount.ShouldBe(3);
        var telegram = advice.Channels.Single(channel => channel.Provider == MessagingConstants.ProviderTelegram);
        telegram.ViaContact.ShouldBe(1);
        telegram.ViaPhone.ShouldBe(0);
        telegram.CanReceiveReplies.ShouldBeTrue();
        var sms = advice.Channels.Single(channel => channel.Provider == MessagingConstants.ProviderSms);
        sms.ViaPhone.ShouldBe(1);
        sms.CanReceiveReplies.ShouldBeFalse();
        advice.MissingCount.ShouldBe(1);
    }

    [Test]
    public async Task NamesAreReadOnlyForTheMissing_AndCapped()
    {
        var advice = await _sut.EvaluateAsync(Group(), isAdmin: true);

        advice.MissingNames.ShouldBe(new[] { MissingName });
        await _contacts.Received(1).GetClientDisplayNamesAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { WithNothing })),
            MessagingConstants.MaxReachMissingNames,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheAdvice_CarriesNoAddress()
    {
        var advice = await _sut.EvaluateAsync(Group(), isAdmin: true);

        var json = JsonSerializer.Serialize(advice);

        json.ShouldNotContain(TelegramChat);
        json.ShouldNotContain(MobileNumber);
        json.ShouldNotContain(WithContact.ToString());
    }

    [Test]
    public async Task ASwitchedOffProvider_IsReportedButDoesNotReach()
    {
        GiveProviders((MessagingConstants.ProviderTelegram, false, false));

        var advice = await _sut.EvaluateAsync(Group(), isAdmin: true);

        advice.ReachableCount.ShouldBe(0);
        advice.Findings.ShouldContain(new MessagingReachFinding(
            MessagingReachFindingCodes.ProviderDisabled, 1, MessagingConstants.ProviderTelegram));
    }

    [Test]
    public async Task AGroupANonAdminMayNotSee_ReadsLikeAnEmptyOne_AndTouchesNothing()
    {
        _accessScope.GetVisibleGroupClientIdsAsync(GroupId, Arg.Any<CancellationToken>()).Returns(Array.Empty<Guid>());

        var advice = await _sut.EvaluateAsync(Group(), isAdmin: false);

        advice.AudienceCount.ShouldBe(0);
        advice.Findings.ShouldHaveSingleItem().Code.ShouldBe(MessagingReachFindingCodes.NoVisibleClients);
        await _groupReader.DidNotReceiveWithAnyArgs().GetClientIdsInGroupAsync(default);
        await _contacts.DidNotReceiveWithAnyArgs().GetClientDisplayNamesAsync(default!, default, default);
    }

    [Test]
    public async Task ANonAdmin_CountsOnlyVisiblePeople()
    {
        _accessScope.GetVisibleGroupClientIdsAsync(GroupId, Arg.Any<CancellationToken>()).Returns(new[] { WithContact });

        var advice = await _sut.EvaluateAsync(Group(), isAdmin: false);

        advice.AudienceCount.ShouldBe(1);
        advice.MissingCount.ShouldBe(0);
        advice.MissingNames.ShouldBeEmpty();
    }

    [Test]
    public async Task TheSkill_RelaysTheComputedRecommendation()
    {
        var skill = new EvaluateMessagingReachSkill(_sut);
        var context = new Klacks.Plugin.Contracts.Skills.SkillExecutionContext
        {
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            UserName = "admin",
            UserPermissions = new[] { MessagingConstants.RoleAdmin }
        };

        var result = await skill.ExecuteAsync(context, new Dictionary<string, object> { ["groupId"] = GroupId.ToString() });

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe(((MessagingReachAdvice)result.Data!).Recommendation);
    }

    [Test]
    public async Task TheSkill_RefusesACallWithoutAnAudience()
    {
        var skill = new EvaluateMessagingReachSkill(Substitute.For<IMessagingReachService>());
        var context = new Klacks.Plugin.Contracts.Skills.SkillExecutionContext
        {
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            UserName = "admin",
            UserPermissions = new[] { MessagingConstants.RoleAdmin }
        };

        var result = await skill.ExecuteAsync(context, new Dictionary<string, object>());

        result.Success.ShouldBeFalse();
    }
}
