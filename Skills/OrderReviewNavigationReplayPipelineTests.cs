// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The navigation of execute_order_review survives its only real path, the confirmed replay: turn one holds the call with
/// the REAL gate and the REAL preview provider, turn two redeems it with an explicit yes through the REAL confirm skill and
/// the REAL skill executor with the REAL entity-change notifier (planner, order executor and notification hub are
/// doubles). The confirm result is the inner result: Data still carries the top-level Route (so the chat navigates after
/// the turn) and the replayed skill name, and the entity-changed "shift" is pushed under execute_order_review, not
/// confirm_pending_action.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Orders.Review;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Services.Assistant.Autonomy;
using Klacks.Api.Application.Services.Orders.Review;
using Klacks.Api.Application.Skills;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Application.Skills.OrderReview;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.OrderReview;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class OrderReviewNavigationReplayPipelineTests
{
    private const string ConfirmationTokenKey = "confirmationToken";
    private const string SessionId = "conversation-replay";

    private IAgentAutonomyPreferenceRepository _preferences = null!;
    private IPendingConfirmationStore _store = null!;
    private IOrderReviewExecutionPlanner _planner = null!;
    private IOrderReviewExecutor _orderExecutor = null!;
    private IAssistantNotificationService _notifications = null!;
    private SkillExecutorService _executor = null!;
    private Guid _userId;

    [SetUp]
    public void SetUp()
    {
        _userId = Guid.NewGuid();
        _preferences = Substitute.For<IAgentAutonomyPreferenceRepository>();
        _preferences.GetAsync(_userId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = _userId.ToString(), Level = AutonomyLevel.FullyAutonomous });
        _store = PendingStoreTestFactory.CreateConfirmationStore();

        var target = new OrderReviewExecutionTarget(
            new OrderReviewDraftItem { OrderId = Guid.NewGuid(), OrderName = "Wache A", ReviewKey = "B1" },
            new Shift
            {
                Name = "Wache A", Status = ShiftStatus.OriginalOrder,
                FromDate = new DateOnly(2026, 11, 2), UntilDate = new DateOnly(2026, 11, 8)
            });
        var draft = new OrderReviewDraft { Items = [target.Item] };
        var plan = new OrderReviewExecutionPlan(null, draft, [target], 0, 0, OrderReviewBinding.Of([target]), "preview text");
        _planner = Substitute.For<IOrderReviewExecutionPlanner>();
        _planner.PlanAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(plan);
        _orderExecutor = Substitute.For<IOrderReviewExecutor>();
        _orderExecutor.ExecuteAsync(target, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new OrderReviewExecutionOutcome(target.Item.OrderId, true, Guid.NewGuid(), 3, null));
        _notifications = Substitute.For<IAssistantNotificationService>();

        var draftStore = PendingStoreTestFactory.CreateOrderReviewDraftStore();
        var services = new ServiceCollection();
        services.AddScoped(_ => new ExecuteOrderReviewSkill(_planner, _orderExecutor, draftStore));
        var registry = Substitute.For<ISkillRegistry>();
        registry.GetSkillByName(ExecuteOrderReviewSkill.SkillName)
            .Returns(Descriptor() with { ImplementationType = typeof(ExecuteOrderReviewSkill) });
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
            new EntityChangeNotifier(new SkillRiskClassifier(), _notifications, NullLogger<EntityChangeNotifier>.Instance),
            Substitute.For<IRecentEntityRegistrar>(),
            scopeGuard,
            Substitute.For<ILogger<SkillExecutorService>>());
    }

    private static SkillDescriptor Descriptor() =>
        new(ExecuteOrderReviewSkill.SkillName, "test", SkillCategory.Crud, [], [], [], null);

    private SkillExecutionContext Context(string userMessage) => new()
    {
        UserId = _userId,
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserLanguage = "de",
        SessionId = SessionId,
        UserPermissions = Permissions.ExpandRoles([Roles.Admin]),
        UserConfirmation = AffirmationDetector.IsExplicitConfirmation(userMessage, "de")
            ? UserConfirmationEvidence.ExplicitConfirmationReply
            : UserConfirmationEvidence.None
    };

    private (AutonomyGateService Gate, ConfirmPendingActionSkill Confirm) NewTurn()
    {
        var scope = new TurnConfirmationScope();
        var guard = new ConfirmationRedemptionGuard(scope);
        var gate = new AutonomyGateService(
            _preferences, new SkillRiskClassifier(), _store, scope, guard,
            [new OrderReviewConfirmationPreviewProvider(_planner)], NullLogger<AutonomyGateService>.Instance);
        return (gate, new ConfirmPendingActionSkill(_store, _executor, guard));
    }

    [Test]
    public async Task TheConfirmedReplay_KeepsTheRoute_AndNotifiesUnderExecuteOrderReview()
    {
        var held = await NewTurn().Gate.CheckAsync(
            Descriptor(), Context("Passt, führ es aus"), new Dictionary<string, object> { ["target"] = "B1" });
        held.ShouldNotBeNull();
        held.Type.ShouldBe(SkillResultType.Confirmation);
        var token = (string)held.Metadata![ConfirmationTokenKey];

        var confirm = await NewTurn().Confirm.ExecuteAsync(
            Context("ja"), new Dictionary<string, object> { [AutonomyDefaults.ConfirmationTokenParameter] = token });

        confirm.Success.ShouldBeTrue(confirm.Message);
        confirm.Type.ShouldBe(SkillResultType.Data);
        var data = JsonSerializer.SerializeToElement(confirm.Data);
        data.GetProperty("Route").GetString().ShouldBe("/workplace/shift?view=plannable&from=2026-11-02&until=2026-11-08");
        confirm.Metadata![SkillResultMetadataKeys.ReplayedSkillName].ShouldBe(ExecuteOrderReviewSkill.SkillName);
        await _notifications.Received(1).SendEntityChangedAsync(
            _userId.ToString(),
            Arg.Is<IReadOnlyList<string>>(entities => entities.SequenceEqual(new[] { "shift" })),
            "update",
            ExecuteOrderReviewSkill.SkillName);
    }
}
