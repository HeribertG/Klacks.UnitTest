// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// End to end across turns with the REAL gate, preview provider, planner, audience and address resolution, send_broadcast skill,
/// skill executor, confirm skill, risk classifier and pending store (only the messaging service's delivery and the data readers
/// are doubles, so nothing can be sent): turn one holds the call at the highest autonomy level and shows count, channel and text
/// but no address; turn two redeems it with an explicit yes and sends exactly once to the previewed addresses. A member who joined
/// after the preview sends nothing; a second pending confirmation of the same broadcast sends nothing; more recipients than the host
/// keeps binding items for still match; a call that skips the gate never sends; an audience a non-admin may not see gets no token.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Autonomy;
using Klacks.Api.Application.Skills;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Application.Services;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Skills;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SendBroadcastPreviewPipelineTests
{
    private const string ConfirmationTokenKey = "confirmationToken";
    private const string Text = "Das Buero bleibt morgen geschlossen.";
    private const string Provider = "Telegram";
    private const int MoreRecipientsThanTheHostStoresItems = ConfirmationBindingDefaults.MaxStoredItems + 25;

    private IAgentAutonomyPreferenceRepository _preferences = null!;
    private IPendingConfirmationStore _store = null!;
    private IMessagingService _messagingService = null!;
    private IMessageRepository _messages = null!;
    private IClientGroupReader _groupReader = null!;
    private IMessagingAccessScope _accessScope = null!;
    private BroadcastPlanner _planner = null!;
    private SkillExecutorService _executor = null!;
    private Guid _userId;
    private Guid _groupId;
    private List<Guid> _members = null!;
    private Dictionary<Guid, string> _addresses = null!;
    private IReadOnlySet<Guid> _delivered = null!;

    [SetUp]
    public void SetUp()
    {
        _userId = Guid.NewGuid();
        _groupId = Guid.NewGuid();
        _members = [Guid.NewGuid(), Guid.NewGuid()];
        _addresses = _members.ToDictionary(id => id, id => "chat-" + id.ToString("N"));
        _delivered = new HashSet<Guid>();

        _preferences = Substitute.For<IAgentAutonomyPreferenceRepository>();
        _preferences.GetAsync(_userId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = _userId.ToString(), Level = AutonomyLevel.FullyAutonomous });
        _store = PendingStoreTestFactory.CreateConfirmationStore();

        _groupReader = Substitute.For<IClientGroupReader>();
        _groupReader.GetClientIdsInGroupAsync(_groupId, Arg.Any<CancellationToken>()).Returns(_ => _members.ToList());
        _accessScope = Substitute.For<IMessagingAccessScope>();

        _messagingService = Substitute.For<IMessagingService>();
        _messagingService.ResolveBroadcastRecipientsAsync(Provider, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => Resolve(call.Arg<IReadOnlyCollection<Guid>>()));
        _messagingService.SendBroadcastToRecipientsAsync(
                Arg.Any<BroadcastRecipientSet>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new BroadcastSendResult(Guid.NewGuid(), _members.Count, call.Arg<BroadcastRecipientSet>().Recipients.Count, 0, 0));

        _messages = Substitute.For<IMessageRepository>();
        _messages.GetRecentBroadcastClientIdsAsync(
                Arg.Any<Guid>(), Text, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(_ => _delivered);

        _planner = new BroadcastPlanner(
            new BroadcastAudienceResolver(_groupReader, Substitute.For<IClientIdNumberReader>(), _accessScope),
            _messagingService,
            Substitute.For<IMessagingProviderRepository>(),
            _messages);

        var services = new ServiceCollection();
        services.AddScoped(_ => new SendBroadcastSkill(_planner, _messagingService, Substitute.For<IBroadcastRunLock>()));
        var registry = Substitute.For<ISkillRegistry>();
        registry.GetSkillByName("send_broadcast").Returns(Descriptor() with { ImplementationType = typeof(SendBroadcastSkill) });
        var openGate = Substitute.For<IAutonomyGate>();
        openGate.CheckAsync(
                Arg.Any<SkillDescriptor>(), Arg.Any<SkillExecutionContext>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns((SkillResult?)null);
        var scopeGuard = Substitute.For<ISkillScenarioScopeGuard>();
        scopeGuard.CheckAsync(Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>()).Returns((SkillResult?)null);
        _executor = new SkillExecutorService(
            registry,
            Substitute.For<ISkillUsageTracker>(),
            services.BuildServiceProvider().CreateScope().ServiceProvider,
            Substitute.For<IGenericSkillDispatcher>(),
            openGate,
            Substitute.For<IEntityChangeNotifier>(),
            Substitute.For<IRecentEntityRegistrar>(),
            scopeGuard,
            Substitute.For<ILogger<SkillExecutorService>>());
    }

    private BroadcastRecipientSet Resolve(IReadOnlyCollection<Guid> clientIds) =>
        new(
            Guid.Empty,
            Provider,
            clientIds.Select(id => new BroadcastRecipient(id, _addresses[id], null, FromPhoneFallback: false)).ToList(),
            0);

    private static SkillDescriptor Descriptor() =>
        new("send_broadcast", "test", SkillCategory.Action, [], [], [], null);

    private Dictionary<string, object> CallParameters() => new()
    {
        ["provider"] = Provider,
        ["groupId"] = _groupId.ToString(),
        ["content"] = Text
    };

    private SkillExecutionContext Context(string userMessage, bool admin = true) => new()
    {
        UserId = _userId,
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = admin
            ? new List<string> { Roles.Admin }
            : new List<string> { Roles.Authorised, Permissions.CanEditClients },
        UserConfirmation = AffirmationDetector.IsExplicitConfirmation(userMessage)
            ? UserConfirmationEvidence.ExplicitConfirmationReply
            : UserConfirmationEvidence.None
    };

    private (AutonomyGateService Gate, ConfirmPendingActionSkill Confirm) NewTurn()
    {
        var scope = new TurnConfirmationScope();
        var guard = new ConfirmationRedemptionGuard(scope);
        var gate = new AutonomyGateService(
            _preferences, new SkillRiskClassifier(), _store, scope, guard,
            [new SendBroadcastConfirmationPreviewProvider(_planner)], NullLogger<AutonomyGateService>.Instance);
        return (gate, new ConfirmPendingActionSkill(_store, _executor, guard));
    }

    private static Dictionary<string, object> Redeem(string token) =>
        new() { [AutonomyDefaults.ConfirmationTokenParameter] = token };

    private async Task<string> HoldAsync()
    {
        var held = await NewTurn().Gate.CheckAsync(Descriptor(), Context("Schick eine Rundsendung"), CallParameters());

        held.ShouldNotBeNull();
        held.Type.ShouldBe(SkillResultType.Confirmation);
        return (string)held.Metadata![ConfirmationTokenKey];
    }

    private Task NothingWasSent() =>
        _messagingService.DidNotReceiveWithAnyArgs().SendBroadcastToRecipientsAsync(default!, default!, default!, default);

    [Test]
    public async Task TheCallIsHeldAtTheHighestAutonomyLevel_ShowingCountChannelAndText_ButNoAddress()
    {
        var held = await NewTurn().Gate.CheckAsync(Descriptor(), Context("Schick eine Rundsendung"), CallParameters());

        held.ShouldNotBeNull();
        held.Type.ShouldBe(SkillResultType.Confirmation);
        held.Message!.ShouldContain(Provider);
        held.Message.ShouldContain("2 recipient(s)");
        held.Message.ShouldContain(Text);
        foreach (var address in _addresses.Values)
        {
            held.Message.ShouldNotContain(address);
        }

        await NothingWasSent();
    }

    [Test]
    public async Task TheConfirmedBroadcast_IsSentExactlyOnce_ToThePreviewedAddresses()
    {
        var token = await HoldAsync();

        var confirm = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(token));

        confirm.Success.ShouldBeTrue(confirm.Message);
        await _messagingService.Received(1).SendBroadcastToRecipientsAsync(
            Arg.Is<BroadcastRecipientSet>(set => set.Recipients.Select(r => r.Recipient).ToHashSet().SetEquals(_addresses.Values)),
            Text,
            MessagingConstants.DefaultContentType,
            Arg.Any<CancellationToken>());

        var again = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(token));
        again.Success.ShouldBeFalse();
        await _messagingService.ReceivedWithAnyArgs(1).SendBroadcastToRecipientsAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task AYesWithoutConfirmationEvidence_SendsNothing()
    {
        var token = await HoldAsync();

        var confirm = await NewTurn().Confirm.ExecuteAsync(Context("wie viele sind das?"), Redeem(token));

        confirm.Success.ShouldBeFalse();
        await NothingWasSent();
    }

    [Test]
    public async Task AMemberWhoJoinedAfterThePreview_SendsNothing_AndSpendsTheToken()
    {
        var token = await HoldAsync();
        var newcomer = Guid.NewGuid();
        _members.Add(newcomer);
        _addresses[newcomer] = "chat-newcomer";

        var confirm = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(token));

        confirm.Success.ShouldBeFalse();
        confirm.Message.ShouldBe(ConfirmationBindingMessages.ScopeChanged);
        await NothingWasSent();
    }

    [Test]
    public async Task AnAddressChangedAfterThePreview_SendsNothing()
    {
        var token = await HoldAsync();
        _addresses[_members[0]] = "chat-OTHER";

        var confirm = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(token));

        confirm.Success.ShouldBeFalse();
        confirm.Message.ShouldBe(ConfirmationBindingMessages.ScopeChanged);
        await NothingWasSent();
    }

    [Test]
    public async Task MoreRecipientsThanTheHostStoresItems_StillMatchAfterTheRoundTrip()
    {
        _members = Enumerable.Range(0, MoreRecipientsThanTheHostStoresItems).Select(_ => Guid.NewGuid()).ToList();
        _addresses = _members.ToDictionary(id => id, id => "chat-" + id.ToString("N"));
        var token = await HoldAsync();

        var confirm = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(token));

        confirm.Success.ShouldBeTrue(confirm.Message);
        await _messagingService.Received(1).SendBroadcastToRecipientsAsync(
            Arg.Is<BroadcastRecipientSet>(set => set.Recipients.Count == MoreRecipientsThanTheHostStoresItems),
            Text,
            MessagingConstants.DefaultContentType,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ASecondPendingConfirmationOfTheSameBroadcast_SendsNothing()
    {
        var first = await HoldAsync();
        var second = await HoldAsync();

        (await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(first))).Success.ShouldBeTrue();
        _delivered = _members.ToHashSet();

        var repeated = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(second));

        repeated.Success.ShouldBeFalse();
        repeated.Message.ShouldBe(MessagingConstants.BroadcastAlreadyDeliveredError);
        await _messagingService.ReceivedWithAnyArgs(1).SendBroadcastToRecipientsAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task ACallThatSkipsTheGate_NeverSends()
    {
        var bypass = Context("ja") with { BypassAutonomyGate = true };

        var result = await _executor.ExecuteAsync(
            new SkillInvocation { SkillName = "send_broadcast", Parameters = CallParameters() }, bypass);

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(string.Format(ConfirmationBindingMessages.BindingRequired, "send_broadcast"));
        await NothingWasSent();
    }

    [Test]
    public async Task AGroupANonAdminMayNotSee_IsRefusedLikeAnEmptyOne_AndIssuesNoToken()
    {
        _accessScope.GetVisibleGroupClientIdsAsync(_groupId, Arg.Any<CancellationToken>()).Returns(Array.Empty<Guid>());

        var refused = await NewTurn().Gate.CheckAsync(Descriptor(), Context("Schick eine Rundsendung", admin: false), CallParameters());

        refused.ShouldNotBeNull();
        refused.Type.ShouldBe(SkillResultType.Error);
        refused.Message.ShouldBe(MessagingConstants.BroadcastGroupEmptyError);
        _store.PeekLatestForUser(_userId, TimeSpan.FromMinutes(AutonomyDefaults.ConfirmationTtlMinutes)).ShouldBeNull();
        await _groupReader.DidNotReceiveWithAnyArgs().GetClientIdsInGroupAsync(default, default);
        await NothingWasSent();
    }

    [Test]
    public async Task ADisabledProvider_IsRefusedAtThePreview()
    {
        _messagingService.ResolveBroadcastRecipientsAsync(Provider, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns<Task<BroadcastRecipientSet>>(_ => throw new InvalidOperationException("Provider 'Telegram' is disabled"));

        var refused = await NewTurn().Gate.CheckAsync(Descriptor(), Context("Schick eine Rundsendung"), CallParameters());

        refused.ShouldNotBeNull();
        refused.Type.ShouldBe(SkillResultType.Error);
        refused.Message!.ShouldContain("disabled");
    }
}
