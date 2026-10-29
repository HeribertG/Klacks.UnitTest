// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The order review navigates the UI: a started review carries Route /workplace/shift?view=orders in its Data, a scope
/// without open orders none; a confirmed execute_order_review carries the shift list on the plannable shifts of the
/// executed orders (from the earliest start to the latest end, no until when one order is open-ended), plus the group
/// most of them are staffed from and the first day for open_schedule, and says nobody is assigned. Nothing executed
/// means no Route. execute_order_review is mapped to Shift, so its success pushes an entity-changed "shift".
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Orders;
using Klacks.Api.Application.DTOs.Orders.Review;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Services.Assistant.SkillGraph;
using Klacks.Api.Application.Services.Orders.Review;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Application.Skills.OrderReview;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.OrderReview;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class OrderReviewNavigationTests
{
    private const string SessionId = "conversation-nav";
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid Bern = Guid.NewGuid();
    private static readonly Guid Thun = Guid.NewGuid();

    private IPendingOrderReviewDraftStore _store = null!;
    private IOrderReviewLoader _loader = null!;
    private IOrderReviewExecutionPlanner _planner = null!;
    private IOrderReviewExecutor _executor = null!;

    [SetUp]
    public void SetUp()
    {
        _store = PendingStoreTestFactory.CreateOrderReviewDraftStore();
        _loader = Substitute.For<IOrderReviewLoader>();
        _loader.ResolveScopeAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new OrderReviewScope(null, true, [], [], OpenOrderSealingScope.FullAccess()));
        _planner = Substitute.For<IOrderReviewExecutionPlanner>();
        _executor = Substitute.For<IOrderReviewExecutor>();
    }

    private static SkillExecutionContext Ctx(ConfirmationBinding? binding = null) => new()
    {
        UserId = UserId,
        TenantId = Guid.NewGuid(),
        UserName = "planner",
        SessionId = SessionId,
        UserLanguage = "de",
        UserPermissions = ["CanEditShifts", Roles.Admin],
        ConfirmedActionBinding = binding,
    };

    private static JsonElement Data(SkillResult result) =>
        JsonSerializer.SerializeToElement(result.Data);

    private static string? Route(SkillResult result) =>
        Data(result).TryGetProperty("Route", out var route) && route.ValueKind == JsonValueKind.String
            ? route.GetString()
            : null;

    [Test]
    public async Task Start_WithOpenOrders_RoutesToTheOrdersView()
    {
        _loader.LoadItemsAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<OrderReviewScope>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<OrderReviewDraftItem> { new() { OrderId = Guid.NewGuid(), ReviewKey = "B1" } });

        var result = await new StartOrderReviewSkill(_loader, _store).ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Type.ShouldBe(SkillResultType.Data);
        Route(result).ShouldBe("/workplace/shift?view=orders");
    }

    [Test]
    public async Task Start_WithoutOpenOrders_DoesNotNavigate()
    {
        _loader.LoadItemsAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<OrderReviewScope>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<OrderReviewDraftItem>());

        var result = await new StartOrderReviewSkill(_loader, _store).ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        Route(result).ShouldBeNull();
    }

    [Test]
    public async Task Execute_RoutesToThePlannableShiftsOfTheExecutedOrders_AndOffersTheSchedule()
    {
        var first = Target("Wache A", new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 20), Bern, own: true);
        var second = Target("Wache B", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), Thun, own: false);
        var third = Target("Wache C", new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 6), Bern, own: false);
        var failed = Target("Wache D", new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 31), Thun, own: true);

        var result = await ExecuteAsync([first, second, third], [failed]);

        result.Success.ShouldBeTrue();
        result.Type.ShouldBe(SkillResultType.Data);
        Route(result).ShouldBe("/workplace/shift?view=plannable&from=2026-11-01&until=2026-11-30");
        var data = Data(result);
        data.GetProperty("ScheduleGroupId").GetGuid().ShouldBe(Bern);
        data.GetProperty("ScheduleGroupName").GetString().ShouldBe("Bern");
        data.GetProperty("ScheduleDate").GetString().ShouldBe("2026-11-01");
        result.Message.ShouldContain("nobody is assigned");
        result.Message.ShouldContain("open_schedule with groupName 'Bern' and date 2026-11-01");
        result.Message.ShouldContain("opened on the new plannable shift(s) of the period 2026-11-01 to 2026-11-30");
        result.Message.ShouldNotContain("switch");
    }

    [Test]
    public async Task Execute_WithAnOpenEndedOrder_LeavesUntilOut()
    {
        var bounded = Target("Wache A", new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 20), Bern, own: true);
        var openEnded = Target("Wache B", new DateOnly(2026, 11, 4), null, Bern, own: true);

        var result = await ExecuteAsync([bounded, openEnded], []);

        Route(result).ShouldBe("/workplace/shift?view=plannable&from=2026-11-03");
        result.Message.ShouldContain("from 2026-11-03 (open-ended)");
    }

    [Test]
    public async Task Execute_WithoutAGroup_OffersTheScheduleByDateOnly()
    {
        var target = Target("Wache A", new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 20), null, own: false);

        var result = await ExecuteAsync([target], []);

        var data = Data(result);
        data.GetProperty("ScheduleGroupId").ValueKind.ShouldBe(JsonValueKind.Null);
        result.Message.ShouldContain("open_schedule with date 2026-11-03");
    }

    [Test]
    public async Task Execute_WhenEveryOrderFailed_DoesNotNavigate()
    {
        var failed = Target("Wache A", new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 20), Bern, own: true);

        var result = await ExecuteAsync([], [failed]);

        result.Success.ShouldBeTrue();
        Route(result).ShouldBeNull();
        result.Message.ShouldNotContain("open_schedule");
    }

    [Test]
    public void ExecuteOrderReview_IsMappedToShift()
    {
        SkillEntityMap.Map[ExecuteOrderReviewSkill.SkillName].ShouldBe(["Shift"]);
    }

    [Test]
    public async Task ExecuteOrderReview_Success_PushesAShiftEntityChange()
    {
        var notifications = Substitute.For<IAssistantNotificationService>();
        var notifier = new EntityChangeNotifier(
            new SkillRiskClassifier(), notifications, NullLogger<EntityChangeNotifier>.Instance);
        var descriptor = new SkillDescriptor(ExecuteOrderReviewSkill.SkillName, "test", SkillCategory.Crud, [], [], [], null);

        await notifier.NotifyExecutedAsync(descriptor, Ctx(), SkillResult.SuccessResult(new { Executed = 1 }));

        await notifications.Received(1).SendEntityChangedAsync(
            UserId.ToString(),
            Arg.Is<IReadOnlyList<string>>(entities => entities.SequenceEqual(new[] { "shift" })),
            "update",
            ExecuteOrderReviewSkill.SkillName);
    }

    private async Task<SkillResult> ExecuteAsync(
        IReadOnlyList<OrderReviewExecutionTarget> succeeding, IReadOnlyList<OrderReviewExecutionTarget> failing)
    {
        var targets = succeeding.Concat(failing).ToArray();
        var draft = new OrderReviewDraft { Items = targets.Select(target => target.Item).ToList() };
        var plan = new OrderReviewExecutionPlan(null, draft, targets, 0, 0, OrderReviewBinding.Of(targets), "preview");
        _planner.PlanAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(plan);
        foreach (var target in succeeding)
        {
            _executor.ExecuteAsync(target, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new OrderReviewExecutionOutcome(target.Item.OrderId, true, Guid.NewGuid(), 3, null));
        }

        foreach (var target in failing)
        {
            _executor.ExecuteAsync(target, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new OrderReviewExecutionOutcome(target.Item.OrderId, false, null, 0, "cut refused"));
        }

        return await new ExecuteOrderReviewSkill(_planner, _executor, _store)
            .ExecuteAsync(Ctx(plan.Binding), new Dictionary<string, object> { ["target"] = "all" });
    }

    private static OrderReviewExecutionTarget Target(
        string name, DateOnly from, DateOnly? until, Guid? groupId, bool own)
    {
        var groupName = groupId == Bern ? "Bern" : groupId == Thun ? "Thun" : null;
        var item = new OrderReviewDraftItem { OrderId = Guid.NewGuid(), OrderName = name, ReviewKey = "B1", HasGroup = own };
        if (own)
        {
            item.CurrentGroupId = groupId;
            item.CurrentGroupName = groupName;
        }
        else
        {
            item.ChosenGroupId = groupId;
            item.ChosenGroupName = groupName;
        }

        return new OrderReviewExecutionTarget(
            item,
            new Shift { Id = item.OrderId, Name = name, Status = ShiftStatus.OriginalOrder, FromDate = from, UntilDate = until });
    }
}
