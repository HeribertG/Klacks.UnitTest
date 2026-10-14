// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// End to end across turns with the REAL gate, the REAL preview provider, the REAL recipient resolver and send_message
/// plugin skill, the REAL skill executor (which hands the binding on to the plugin context), the REAL confirm skill, the real
/// risk classifier and the real pending store (only the messaging service and the contact lookups are doubles): turn one holds
/// the call and shows channel, recipient and text, turn two redeems it with an explicit yes and sends exactly once to the
/// previewed address. A contact whose address changed in between sends nothing and spends the token; a token sent along with
/// the call itself is refused and stays valid; a call that skips the gate (a plan step) never sends; a recipient a
/// non-admin may not reach is refused at the preview, so no token exists.
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
public class SendMessagePreviewPipelineTests
{
    private const string ConfirmationTokenKey = "confirmationToken";
    private const string Text = "Hallo Hans, die Schicht morgen faellt aus.";

    private IAgentAutonomyPreferenceRepository _preferences = null!;
    private IPendingConfirmationStore _store = null!;
    private IMessagingService _messagingService = null!;
    private IMessengerContactRepository _contacts = null!;
    private IMessagingAccessScope _accessScope = null!;
    private MessageRecipientResolver _resolver = null!;
    private SkillExecutorService _executor = null!;
    private Guid _userId;
    private Guid _clientId;
    private string _chatId = null!;

    [SetUp]
    public void SetUp()
    {
        _userId = Guid.NewGuid();
        _clientId = Guid.NewGuid();
        _chatId = "chat-123";
        _preferences = Substitute.For<IAgentAutonomyPreferenceRepository>();
        _preferences.GetAsync(_userId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = _userId.ToString(), Level = AutonomyLevel.FullyAutonomous });
        _store = PendingStoreTestFactory.CreateConfirmationStore();
        _messagingService = Substitute.For<IMessagingService>();
        _messagingService.SendMessageAsync(Arg.Any<string>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SendMessageResult(true, ExternalMessageId: "ext-1"));
        _messagingService.ResolveRecipientClientIdAsync("telegram", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => (Guid?)_clientId);
        _accessScope = Substitute.For<IMessagingAccessScope>();
        _accessScope.IsClientVisibleAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(true);
        _contacts = Substitute.For<IMessengerContactRepository>();
        _contacts.SearchByClientNameAsync("Hans Muster", MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(_ => new[] { new ClientMessengerMatch { ClientId = _clientId, Value = _chatId, FirstName = "Hans", Name = "Muster" } });
        _resolver = new MessageRecipientResolver(
            _messagingService,
            _contacts,
            Substitute.For<IOwnerMessengerReader>(),
            Substitute.For<IAppUserDirectoryReader>(),
            Substitute.For<IUserMessengerContactRepository>(),
            Substitute.For<IMessagingProviderRepository>(),
            _accessScope);

        var services = new ServiceCollection();
        services.AddScoped(_ => new SendMessageSkill(_messagingService, _resolver));
        var registry = Substitute.For<ISkillRegistry>();
        registry.GetSkillByName("send_message").Returns(Descriptor() with { ImplementationType = typeof(SendMessageSkill) });
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

    private static SkillDescriptor Descriptor() =>
        new("send_message", "test", SkillCategory.Action, [], [], [], null);

    private static Dictionary<string, object> CallParameters() => new()
    {
        ["provider"] = "telegram",
        ["recipient"] = "Hans Muster",
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
            [new SendMessageConfirmationPreviewProvider(_resolver)], NullLogger<AutonomyGateService>.Instance);
        return (gate, new ConfirmPendingActionSkill(_store, _executor, guard));
    }

    private static Dictionary<string, object> Redeem(string token) =>
        new() { [AutonomyDefaults.ConfirmationTokenParameter] = token };

    private async Task<string> HoldAsync(bool admin = true)
    {
        var held = await NewTurn().Gate.CheckAsync(Descriptor(), Context("Schick Hans eine Nachricht", admin), CallParameters());

        held.ShouldNotBeNull();
        held.Type.ShouldBe(SkillResultType.Confirmation);
        held.Message!.ShouldContain("Muster, Hans");
        held.Message.ShouldContain(_chatId);
        held.Message.ShouldContain(Text);
        return (string)held.Metadata![ConfirmationTokenKey];
    }

    [Test]
    public async Task TheConfirmedMessage_IsSentExactlyOnce_ToThePreviewedAddress()
    {
        var token = await HoldAsync();

        var confirm = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(token));

        confirm.Success.ShouldBeTrue(confirm.Message);
        await _messagingService.Received(1).SendMessageAsync(
            "telegram", Arg.Is<SendMessageRequest>(r => r.Recipient == "chat-123" && r.Content == Text), Arg.Any<CancellationToken>());

        var again = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(token));
        again.Success.ShouldBeFalse();
        await _messagingService.Received(1).SendMessageAsync(
            Arg.Any<string>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AContactAddressChangedAfterThePreview_SendsNothing_AndSpendsTheToken()
    {
        var token = await HoldAsync();
        _chatId = "chat-OTHER";

        var confirm = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(token));

        confirm.Success.ShouldBeFalse();
        confirm.Message.ShouldBe(ConfirmationBindingMessages.ScopeChanged);
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);

        var spent = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(token));
        spent.Success.ShouldBeFalse();
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task ATokenSentAlongWithTheCall_IsRefused_NothingIsSent_AndTheTokenStaysValid()
    {
        var token = await HoldAsync();
        var parameters = CallParameters();
        parameters[AutonomyDefaults.ConfirmationTokenParameter] = token;

        var direct = await NewTurn().Gate.CheckAsync(Descriptor(), Context("ja"), parameters);

        direct.ShouldNotBeNull();
        direct.Success.ShouldBeFalse();
        direct.Message.ShouldBe(string.Format(ConfirmationBindingMessages.DirectRedemptionRefused, "send_message"));
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);

        var confirm = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(token));
        confirm.Success.ShouldBeTrue(confirm.Message);
    }

    [Test]
    public async Task ACallThatSkipsTheGate_NeverSends_AndSaysWhy()
    {
        var bypass = Context("ja") with { BypassAutonomyGate = true };

        var result = await _executor.ExecuteAsync(
            new SkillInvocation { SkillName = "send_message", Parameters = CallParameters() }, bypass);

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(string.Format(ConfirmationBindingMessages.BindingRequired, "send_message"));
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task ASupervisorsHiddenRecipient_IsRefusedAtThePreview_SoNoTokenExists()
    {
        _accessScope.IsClientVisibleAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(false);

        var held = await NewTurn().Gate.CheckAsync(
            Descriptor(), Context("Schick Hans eine Nachricht", admin: false), CallParameters());

        held.ShouldNotBeNull();
        held.Success.ShouldBeFalse();
        held.Type.ShouldNotBe(SkillResultType.Confirmation);
        held.Message.ShouldContain("No Telegram contact found");
        held.Metadata?.ContainsKey(ConfirmationTokenKey).ShouldNotBe(true);
    }
}
