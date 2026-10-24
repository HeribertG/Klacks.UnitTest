// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for OrderReviewExecutionPlanner: only ready orders are planned; a start change needs
/// acknowledgedOrderChange=true and the role Admin or Authorised; an order sealed since the review started refuses the
/// plan; the preview text names the changes to the orders and the expenses an order already carries; the binding
/// changes when a decided expense changes, so a confirmation cannot be replayed with other decisions.
/// </summary>

using Klacks.Api.Application.DTOs.Orders;
using Klacks.Api.Application.DTOs.Orders.Review;
using Klacks.Api.Application.Services.Orders.Review;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant.OrderReview;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Services.Orders.Review;

[TestFixture]
public class OrderReviewExecutionPlannerTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private const string SessionId = "conversation-plan";
    private static readonly TimeOnly Seven = new(7, 0);

    private IPendingOrderReviewDraftStore _store = null!;
    private IOrderReviewLoader _loader = null!;
    private IShiftRepository _shiftRepository = null!;
    private OrderReviewExecutionPlanner _planner = null!;

    [SetUp]
    public void SetUp()
    {
        _store = PendingStoreTestFactory.CreateOrderReviewDraftStore();
        _loader = Substitute.For<IOrderReviewLoader>();
        _loader.ResolveScopeAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new OrderReviewScope(null, true, [], [], OpenOrderSealingScope.FullAccess()));
        _shiftRepository = Substitute.For<IShiftRepository>();
        _planner = new OrderReviewExecutionPlanner(_store, _loader, _shiftRepository, ShiftVisibilityGuards.AllowAll());
    }

    private static SkillExecutionContext Ctx(params string[] roles) => new()
    {
        UserId = UserId,
        TenantId = Guid.NewGuid(),
        UserName = "planner",
        SessionId = SessionId,
        UserPermissions = ["CanEditShifts", .. roles],
    };

    private OrderReviewDraftItem ReadyItem(bool changesStart = true)
    {
        var item = new OrderReviewDraftItem
        {
            OrderId = Guid.NewGuid(),
            OrderName = "Nachtwache",
            ReviewKey = "B1",
            IsFullDay = true,
            CurrentStart = changesStart ? TimeOnly.MinValue : Seven,
            CurrentEnd = changesStart ? TimeOnly.MinValue : Seven,
            ChosenStart = Seven,
            ChosenParts = FullDayOrderRule.EqualParts(Seven, 3),
            HasGroup = true,
            CurrentGroupName = "Bern",
            ChosenQualificationIds = [],
            ExpenseStatus = OrderReviewExpenseStatus.Provided,
            Expenses = [new OrderReviewExpense { Description = "Mahlzeit", Amount = 15m }],
        };
        _shiftRepository.Get(item.OrderId).Returns(new Shift
        {
            Id = item.OrderId,
            Name = item.OrderName,
            Status = ShiftStatus.OriginalOrder,
            ShiftExpenses = [new ShiftExpenses { Description = "Parkplatz", Amount = 5m }],
        });
        return item;
    }

    private void Store(params OrderReviewDraftItem[] items) =>
        _store.Set(UserId, SessionId, new OrderReviewDraft { Items = items.ToList() });

    private static Dictionary<string, object> Params(bool acknowledged) =>
        new() { ["target"] = "B1", ["acknowledgedOrderChange"] = acknowledged };

    [Test]
    public async Task AStartChange_WithoutAcknowledgement_IsRefused()
    {
        Store(ReadyItem());

        var plan = await _planner.PlanAsync(Ctx(Roles.Admin), Params(acknowledged: false));

        Assert.That(plan.Refusal, Does.Contain("acknowledgedOrderChange"));
    }

    [Test]
    public async Task AStartChange_ByAPlannerWithoutRole_IsRefused()
    {
        Store(ReadyItem());

        var plan = await _planner.PlanAsync(Ctx(), Params(acknowledged: true));

        Assert.That(plan.Refusal, Does.Contain("administrators and supervisors"));
    }

    [Test]
    public async Task ASupervisor_GetsThePlan_WithTheChangesToTheOrdersAndTheExistingExpenses()
    {
        Store(ReadyItem());

        var plan = await _planner.PlanAsync(Ctx(Roles.Authorised), Params(acknowledged: true));

        Assert.That(plan.IsRefused, Is.False, plan.Refusal);
        Assert.That(plan.Targets, Has.Count.EqualTo(1));
        Assert.That(plan.Text, Does.Contain("Changes to orders:"));
        Assert.That(plan.Text, Does.Contain("start 00:00 -> 07:00"));
        Assert.That(plan.Text, Does.Contain("Mahlzeit 15"));
        Assert.That(plan.Text, Does.Contain("already carries default expenses Parkplatz 5"));
        Assert.That(plan.Binding!.Kind, Is.EqualTo(ConfirmationBindingKinds.OrderReview));
    }

    [Test]
    public async Task WithoutAStartChange_NoAcknowledgementIsNeeded()
    {
        Store(ReadyItem(changesStart: false));

        var plan = await _planner.PlanAsync(Ctx(), Params(acknowledged: false));

        Assert.That(plan.IsRefused, Is.False, plan.Refusal);
    }

    [Test]
    public async Task AnOrderSealedSinceTheReviewStarted_RefusesThePlan()
    {
        var item = ReadyItem();
        _shiftRepository.Get(item.OrderId).Returns(new Shift { Id = item.OrderId, Status = ShiftStatus.SealedOrder });
        Store(item);

        var plan = await _planner.PlanAsync(Ctx(Roles.Admin), Params(acknowledged: true));

        Assert.That(plan.Refusal, Does.Contain("no longer an open order"));
    }

    [Test]
    public async Task OrdersWithOpenAspects_AreNotPlanned()
    {
        var item = ReadyItem();
        item.ExpenseStatus = OrderReviewExpenseStatus.Unasked;
        Store(item);

        var plan = await _planner.PlanAsync(Ctx(Roles.Admin), Params(acknowledged: true));

        Assert.That(plan.Refusal, Does.Contain("expenses"));
    }

    [Test]
    public async Task AChangedExpense_ChangesTheBinding()
    {
        var item = ReadyItem();
        Store(item);
        var before = await _planner.PlanAsync(Ctx(Roles.Admin), Params(acknowledged: true));

        item.Expenses[0].Amount = 20m;
        Store(item);
        var after = await _planner.PlanAsync(Ctx(Roles.Admin), Params(acknowledged: true));

        Assert.That(before.Binding!.Matches(after.Binding!), Is.False);
    }
}
