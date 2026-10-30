// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The plan of a send_broadcast call: rights like MessagingController (an administrator reads the group directly, everyone else only
/// through the group visibility and never the raw reader), the empty-audience answers, the sole-provider default, the refusal of an
/// audience without addresses, and the repeat protection: people who already got exactly this text are left out and a plan with
/// nobody left fails.
/// </summary>

using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Application.Models;
using Klacks.Plugin.Messaging.Application.Services;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Domain.Models;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class BroadcastPlannerTests
{
    private const string Text = "Hallo";
    private const string Provider = "Telegram";

    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid First = Guid.NewGuid();
    private static readonly Guid Second = Guid.NewGuid();

    private IClientGroupReader _groupReader = null!;
    private IClientIdNumberReader _idNumberReader = null!;
    private IMessagingAccessScope _accessScope = null!;
    private IMessagingService _messagingService = null!;
    private IMessagingProviderRepository _providers = null!;
    private IMessageRepository _messages = null!;
    private BroadcastPlanner _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _groupReader = Substitute.For<IClientGroupReader>();
        _idNumberReader = Substitute.For<IClientIdNumberReader>();
        _accessScope = Substitute.For<IMessagingAccessScope>();
        _messagingService = Substitute.For<IMessagingService>();
        _providers = Substitute.For<IMessagingProviderRepository>();
        _messages = Substitute.For<IMessageRepository>();
        _messages.GetRecentBroadcastClientIdsAsync(default, default!, default, default).ReturnsForAnyArgs(new HashSet<Guid>());
        _messagingService.ResolveBroadcastRecipientsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => new BroadcastRecipientSet(
                Guid.NewGuid(),
                call.Arg<string>(),
                call.Arg<IReadOnlyCollection<Guid>>().Select(id => new BroadcastRecipient(id, "chat", null, false)).ToList(),
                0));

        _sut = new BroadcastPlanner(
            new BroadcastAudienceResolver(_groupReader, _idNumberReader, _accessScope), _messagingService, _providers, _messages);
    }

    private static SendBroadcastParameters ToGroup(string? provider = Provider) =>
        new(new BroadcastAudienceParameters(GroupId, []), Text, provider);

    [Test]
    public async Task AnAdministrator_ReadsTheGroupDirectly()
    {
        _groupReader.GetClientIdsInGroupAsync(GroupId, Arg.Any<CancellationToken>()).Returns(new[] { First, Second });

        var plan = await _sut.PlanAsync(ToGroup(), isAdmin: true);

        plan.Success.ShouldBeTrue();
        plan.Recipients!.Recipients.Count.ShouldBe(2);
        await _accessScope.DidNotReceiveWithAnyArgs().GetVisibleGroupClientIdsAsync(default);
    }

    [Test]
    public async Task ANonAdmin_GetsOnlyTheVisibleClients_AndNeverTheRawGroup()
    {
        _accessScope.GetVisibleGroupClientIdsAsync(GroupId, Arg.Any<CancellationToken>()).Returns(new[] { First });

        var plan = await _sut.PlanAsync(ToGroup(), isAdmin: false);

        plan.Recipients!.Recipients.Single().ClientId.ShouldBe(First);
        await _groupReader.DidNotReceiveWithAnyArgs().GetClientIdsInGroupAsync(default);
    }

    [Test]
    public async Task ANonAdminsIdNumbers_GoThroughTheVisibility()
    {
        var numbers = new[] { 5002, 5043 };
        _accessScope.GetVisibleIdNumberClientIdsAsync(numbers, Arg.Any<CancellationToken>()).Returns(new[] { Second });

        var plan = await _sut.PlanAsync(
            new SendBroadcastParameters(new BroadcastAudienceParameters(null, numbers), Text, Provider), isAdmin: false);

        plan.Recipients!.Recipients.Single().ClientId.ShouldBe(Second);
        await _idNumberReader.DidNotReceiveWithAnyArgs().GetClientIdsByIdNumbersAsync(default!);
    }

    [Test]
    public async Task AHiddenGroup_FailsWithTheEmptyGroupText()
    {
        _accessScope.GetVisibleGroupClientIdsAsync(GroupId, Arg.Any<CancellationToken>()).Returns(Array.Empty<Guid>());

        var plan = await _sut.PlanAsync(ToGroup(), isAdmin: false);

        plan.Success.ShouldBeFalse();
        plan.Error.ShouldBe(MessagingConstants.BroadcastGroupEmptyError);
        await _messagingService.DidNotReceiveWithAnyArgs().ResolveBroadcastRecipientsAsync(default!, default!);
    }

    [Test]
    public async Task WithoutAProvider_TheSoleEnabledOneIsUsed()
    {
        _providers.GetEnabledAsync().Returns(new[] { new MessagingProvider { Name = "bot", ProviderType = Provider } });
        _groupReader.GetClientIdsInGroupAsync(GroupId, Arg.Any<CancellationToken>()).Returns(new[] { First });

        var plan = await _sut.PlanAsync(ToGroup(provider: null), isAdmin: true);

        plan.Recipients!.ProviderName.ShouldBe(Provider);
    }

    [Test]
    public async Task WithoutAProvider_SeveralEnabledOnes_AskWhichOne()
    {
        _providers.GetEnabledAsync().Returns(new[]
        {
            new MessagingProvider { Name = "a", ProviderType = Provider },
            new MessagingProvider { Name = "b", ProviderType = MessagingConstants.ProviderSlack }
        });

        var plan = await _sut.PlanAsync(ToGroup(provider: null), isAdmin: true);

        plan.Success.ShouldBeFalse();
        plan.Error!.ShouldContain("'provider'");
    }

    [Test]
    public async Task AnAudienceWithoutAnyAddress_Fails()
    {
        _groupReader.GetClientIdsInGroupAsync(GroupId, Arg.Any<CancellationToken>()).Returns(new[] { First });
        _messagingService.ResolveBroadcastRecipientsAsync(Provider, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new BroadcastRecipientSet(Guid.NewGuid(), Provider, [], 1));

        var plan = await _sut.PlanAsync(ToGroup(), isAdmin: true);

        plan.Success.ShouldBeFalse();
        plan.Error!.ShouldContain("No recipients");
    }

    [Test]
    public async Task RecipientsWhoAlreadyGotThisText_AreLeftOut()
    {
        _groupReader.GetClientIdsInGroupAsync(GroupId, Arg.Any<CancellationToken>()).Returns(new[] { First, Second });
        _messages.GetRecentBroadcastClientIdsAsync(Arg.Any<Guid>(), Text, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { First });

        var plan = await _sut.PlanAsync(ToGroup(), isAdmin: true);

        plan.Recipients!.Recipients.Single().ClientId.ShouldBe(Second);
        plan.AlreadyReceived.ShouldBe(1);
    }

    [Test]
    public async Task WhenEverybodyAlreadyGotThisText_ThePlanFails()
    {
        _groupReader.GetClientIdsInGroupAsync(GroupId, Arg.Any<CancellationToken>()).Returns(new[] { First });
        _messages.GetRecentBroadcastClientIdsAsync(Arg.Any<Guid>(), Text, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { First });

        var plan = await _sut.PlanAsync(ToGroup(), isAdmin: true);

        plan.Success.ShouldBeFalse();
        plan.Error.ShouldBe(MessagingConstants.BroadcastAlreadyDeliveredError);
    }
}
