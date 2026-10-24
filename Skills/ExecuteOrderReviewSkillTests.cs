// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for execute_order_review: without a confirmed binding of its own kind (plan step, accepted goal, MCP) it
/// refuses before it reads anything; a binding of another preview is refused with the mismatch text; the confirmed
/// replay executes every planned order, records executed and failed orders in the draft and reports the counts; a
/// failed order does not stop the others. The confirmation preview shows the planner's text and binding or refuses.
/// </summary>

using Klacks.Api.Application.DTOs.Orders.Review;
using Klacks.Api.Application.Services.Orders.Review;
using Klacks.Api.Application.Skills.OrderReview;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant.OrderReview;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ExecuteOrderReviewSkillTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private const string SessionId = "conversation-exec";

    private IOrderReviewExecutionPlanner _planner = null!;
    private IOrderReviewExecutor _executor = null!;
    private IPendingOrderReviewDraftStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _planner = Substitute.For<IOrderReviewExecutionPlanner>();
        _executor = Substitute.For<IOrderReviewExecutor>();
        _store = PendingStoreTestFactory.CreateOrderReviewDraftStore();
    }

    private static SkillExecutionContext Ctx(ConfirmationBinding? binding) => new()
    {
        UserId = UserId,
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        SessionId = SessionId,
        UserPermissions = ["CanEditShifts", Roles.Admin],
        ConfirmedActionBinding = binding,
    };

    private static OrderReviewExecutionTarget Target(string name) =>
        new(new OrderReviewDraftItem { OrderId = Guid.NewGuid(), OrderName = name, ReviewKey = "B1" },
            new Shift { Name = name, Status = ShiftStatus.OriginalOrder });

    private static OrderReviewExecutionPlan Plan(params OrderReviewExecutionTarget[] targets)
    {
        var draft = new OrderReviewDraft { Items = targets.Select(t => t.Item).ToList() };
        return new OrderReviewExecutionPlan(null, draft, targets, 0, 0, OrderReviewBinding.Of(targets), "preview text");
    }

    private static Dictionary<string, object> Params() => new() { ["target"] = "B1" };

    [Test]
    public async Task WithoutAConfirmedBinding_RefusesBeforeReadingAnything()
    {
        var result = await new ExecuteOrderReviewSkill(_planner, _executor, _store).ExecuteAsync(Ctx(null), Params());

        Assert.That(result.Success, Is.False);
        await _planner.DidNotReceiveWithAnyArgs().PlanAsync(default!, default!, default);
    }

    [Test]
    public async Task ABindingOfAnotherKind_IsRefusedBeforeReading()
    {
        var foreign = ConfirmationBinding.Of(ConfirmationBindingKinds.OrderSeal, ["x"]);

        var result = await new ExecuteOrderReviewSkill(_planner, _executor, _store).ExecuteAsync(Ctx(foreign), Params());

        Assert.That(result.Success, Is.False);
        await _planner.DidNotReceiveWithAnyArgs().PlanAsync(default!, default!, default);
    }

    [Test]
    public async Task AStaleBinding_IsRefused_AndNothingIsExecuted()
    {
        var shown = Plan(Target("Wache A"));
        var now = Plan(Target("Wache A"), Target("Wache B"));
        _planner.PlanAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(now);

        var result = await new ExecuteOrderReviewSkill(_planner, _executor, _store).ExecuteAsync(Ctx(shown.Binding), Params());

        Assert.That(result.Success, Is.False);
        await _executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!, default, default);
    }

    [Test]
    public async Task TheConfirmedReplay_ExecutesEveryOrder_AndRecordsTheOutcomesInTheDraft()
    {
        var ok = Target("Wache A");
        var failing = Target("Wache B");
        var plan = Plan(ok, failing);
        _planner.PlanAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(plan);
        _executor.ExecuteAsync(ok, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new OrderReviewExecutionOutcome(ok.Item.OrderId, true, Guid.NewGuid(), 3, null));
        _executor.ExecuteAsync(failing, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new OrderReviewExecutionOutcome(failing.Item.OrderId, false, null, 0, "cut refused"));

        var result = await new ExecuteOrderReviewSkill(_planner, _executor, _store).ExecuteAsync(Ctx(plan.Binding), Params());

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("1 changed, sealed and cut"));
        Assert.That(result.Message, Does.Contain("1 failed and rolled back"));
        var stored = _store.Get(UserId, SessionId)!;
        Assert.That(stored.Items.Single(i => i.OrderName == "Wache A").ExecutionStatus, Is.EqualTo(OrderReviewExecutionStatus.Executed));
        var failed = stored.Items.Single(i => i.OrderName == "Wache B");
        Assert.That(failed.ExecutionStatus, Is.EqualTo(OrderReviewExecutionStatus.Failed));
        Assert.That(failed.FailureReason, Is.EqualTo("cut refused"));
    }

    [Test]
    public async Task ThePreview_ShowsThePlannersTextAndBinding_OrRefuses()
    {
        var plan = Plan(Target("Wache A"));
        _planner.PlanAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(plan, OrderReviewExecutionPlan.Refuse("nothing ready"));
        var provider = new OrderReviewConfirmationPreviewProvider(_planner);

        var shown = await provider.BuildAsync(ExecuteOrderReviewSkill.SkillName, Ctx(null), Params());
        var refused = await provider.BuildAsync(ExecuteOrderReviewSkill.SkillName, Ctx(null), Params());

        Assert.That(provider.Supports("execute_order_review"), Is.True);
        Assert.That(shown.IsRefusal, Is.False);
        Assert.That(shown.Text, Is.EqualTo("preview text"));
        Assert.That(shown.Binding, Is.EqualTo(plan.Binding));
        Assert.That(refused.IsRefusal, Is.True);
    }
}
