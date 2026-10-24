// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the order-review loop skills: start_order_review stores the analysed orders as the draft of this
/// conversation and returns the keys, or reports a refused scope and stores nothing; set_order_review_decision needs a
/// review, resolves its target and stores the applier's changes; preview_order_review shows the execution text only for
/// ready orders and names the changes to the orders; cancel_order_review discards the draft.
/// </summary>

using Klacks.Api.Application.DTOs.Orders;
using Klacks.Api.Application.DTOs.Orders.Review;
using Klacks.Api.Application.Services.Orders.Review;
using Klacks.Api.Application.Skills.OrderReview;
using Klacks.Api.Domain.Models.Assistant.OrderReview;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class OrderReviewLoopSkillTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private const string SessionId = "conversation-1";

    private IPendingOrderReviewDraftStore _store = null!;
    private IOrderReviewLoader _loader = null!;
    private IOrderReviewDecisionApplier _applier = null!;

    [SetUp]
    public void SetUp()
    {
        _store = PendingStoreTestFactory.CreateOrderReviewDraftStore();
        _loader = Substitute.For<IOrderReviewLoader>();
        _loader.ResolveScopeAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new OrderReviewScope(null, true, [], [], OpenOrderSealingScope.FullAccess()));
        _applier = Substitute.For<IOrderReviewDecisionApplier>();
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = UserId,
        TenantId = Guid.NewGuid(),
        UserName = "planner",
        SessionId = SessionId,
        UserLanguage = "de",
        UserPermissions = ["CanEditShifts"],
    };

    private static OrderReviewDraftItem Item(string key, bool ready = false)
    {
        var item = new OrderReviewDraftItem
        {
            OrderId = Guid.NewGuid(),
            OrderName = "Wache " + key,
            ReviewKey = key,
            IsFullDay = true,
            CurrentStart = TimeOnly.MinValue,
            CurrentEnd = TimeOnly.MinValue,
            ProposedStart = new TimeOnly(7, 0),
            ProposedParts = FullDayOrderRule.EqualParts(new TimeOnly(7, 0), 3),
            HasGroup = true,
            CurrentGroupName = "Bern",
            ChosenQualificationIds = [],
        };
        if (ready)
        {
            item.ChosenStart = new TimeOnly(7, 0);
            item.ChosenParts = FullDayOrderRule.EqualParts(new TimeOnly(7, 0), 3);
            item.ExpenseStatus = OrderReviewExpenseStatus.DeclinedOnce;
        }

        return item;
    }

    private OrderReviewDraft? Stored() => _store.Get(UserId, SessionId);

    [Test]
    public async Task Start_StoresTheAnalysedOrders_AndReturnsTheKeys()
    {
        _loader.LoadItemsAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<OrderReviewScope>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<OrderReviewDraftItem> { Item("B1"), Item("B1"), Item("B1"), Item("S1") });

        var result = await new StartOrderReviewSkill(_loader, _store).ExecuteAsync(Ctx(), new Dictionary<string, object>());

        Assert.That(result.Success, Is.True);
        Assert.That(Stored()!.Items, Has.Count.EqualTo(4));
        Assert.That(result.Message, Does.Contain("4 open order(s) in 2 review key(s)"));
    }

    [Test]
    public async Task Start_WithARefusedScope_StoresNothing()
    {
        _loader.ResolveScopeAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(OrderReviewScope.Refuse("no scope"));

        var result = await new StartOrderReviewSkill(_loader, _store).ExecuteAsync(Ctx(), new Dictionary<string, object>());

        Assert.That(result.Success, Is.False);
        Assert.That(Stored(), Is.Null);
    }

    [Test]
    public async Task SetDecision_WithoutAReview_IsAnError()
    {
        var result = await new SetOrderReviewDecisionSkill(_store, _loader, _applier)
            .ExecuteAsync(Ctx(), new Dictionary<string, object> { ["target"] = "B1" });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("start_order_review"));
    }

    [Test]
    public async Task SetDecision_StoresWhatTheApplierChanged()
    {
        _store.Set(UserId, SessionId, new OrderReviewDraft { Items = [Item("B1"), Item("S1")] });
        _applier.ApplyAsync(Arg.Any<OrderReviewDraft>(), Arg.Any<IReadOnlyList<OrderReviewDraftItem>>(),
                Arg.Any<OrderReviewDecision>(), Arg.Any<OrderReviewScope>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                foreach (var item in ci.ArgAt<IReadOnlyList<OrderReviewDraftItem>>(1))
                {
                    item.ChosenStart = new TimeOnly(7, 0);
                }

                return new OrderReviewDecisionOutcome();
            });

        var result = await new SetOrderReviewDecisionSkill(_store, _loader, _applier)
            .ExecuteAsync(Ctx(), new Dictionary<string, object> { ["target"] = "b1", ["startTime"] = "07:00" });

        Assert.That(result.Success, Is.True);
        Assert.That(Stored()!.Items.Single(i => i.ReviewKey == "B1").ChosenStart, Is.EqualTo(new TimeOnly(7, 0)));
        Assert.That(Stored()!.Items.Single(i => i.ReviewKey == "S1").ChosenStart, Is.Null);
    }

    [Test]
    public async Task SetDecision_WithAnUnknownTarget_NamesTheKeys()
    {
        _store.Set(UserId, SessionId, new OrderReviewDraft { Items = [Item("B1")] });

        var result = await new SetOrderReviewDecisionSkill(_store, _loader, _applier)
            .ExecuteAsync(Ctx(), new Dictionary<string, object> { ["target"] = "B7" });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("B1"));
    }

    [Test]
    public async Task Preview_DescribesOnlyReadyOrders_WithTheChangesToTheOrders()
    {
        _store.Set(UserId, SessionId, new OrderReviewDraft { Items = [Item("B1", ready: true), Item("S1")] });

        var result = await new PreviewOrderReviewSkill(_store).ExecuteAsync(Ctx(), new Dictionary<string, object>());

        Assert.That(result.Success, Is.True);
        var text = OrderReviewExecutionText.Describe(
            Stored()!.Items.Where(i => i.ReviewKey == "B1").ToList(), 0);
        Assert.That(text, Does.Contain("Changes to orders:"));
        Assert.That(text, Does.Contain("start 00:00 -> 07:00"));
        Assert.That(text, Does.Contain("sealed and cut into 07:00-15:00, 15:00-23:00, 23:00-07:00"));
        Assert.That(result.Message, Does.Contain("execute_order_review"));
    }

    [Test]
    public async Task Cancel_DiscardsTheDraft()
    {
        _store.Set(UserId, SessionId, new OrderReviewDraft { Items = [Item("B1")] });

        var result = await new CancelOrderReviewSkill(_store).ExecuteAsync(Ctx(), new Dictionary<string, object>());

        Assert.That(result.Success, Is.True);
        Assert.That(Stored(), Is.Null);
    }
}
