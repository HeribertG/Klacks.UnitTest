// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for OrderReviewExecutor: the order change (new start, first group, required qualification, expense) is
/// written and saved before the order is sealed inside the same transaction, the plannable shift is cut into exactly the
/// decided parts; a cut the planner refuses or plans differently fails the order, clears the change tracker and is
/// reported instead of thrown; without a split nothing is cut.
/// </summary>

using Klacks.Api.Application.Commands.Shifts;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.DTOs.Orders;
using Klacks.Api.Application.DTOs.Orders.Review;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Services.Orders.Review;
using Klacks.Api.Domain.Models.Assistant.OrderReview;

namespace Klacks.UnitTest.Application.Services.Orders.Review;

[TestFixture]
public class OrderReviewExecutorTests
{
    private static readonly TimeOnly Seven = new(7, 0);
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid QualificationId = Guid.NewGuid();
    private static readonly Guid PlannableShiftId = Guid.NewGuid();

    private IShiftRepository _shiftRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IOrderSealingService _sealing = null!;
    private IShiftTimeCutPlanner _cutPlanner = null!;
    private IShiftCutFacade _cutFacade = null!;
    private OrderReviewExecutor _executor = null!;
    private readonly List<string> _calls = [];

    [SetUp]
    public void SetUp()
    {
        _calls.Clear();
        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftRepository.Put(Arg.Any<Shift>()).Returns(ci => { _calls.Add("put"); return ci.Arg<Shift>(); });
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<(Guid, int)>>>())
            .Returns(ci => ci.Arg<Func<Task<(Guid, int)>>>()());
        _unitOfWork.CompleteAsync().Returns(_ => { _calls.Add("save"); return Task.CompletedTask; });
        _sealing = Substitute.For<IOrderSealingService>();
        _sealing.CheckAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => new OrderSealingCheck(new Shift { Id = ci.Arg<Guid>() }, null));
        _sealing.SealWithinTransactionAsync(Arg.Any<Shift>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _calls.Add("seal"); return PlannableShiftId; });
        _cutPlanner = Substitute.For<IShiftTimeCutPlanner>();
        _cutFacade = Substitute.For<IShiftCutFacade>();
        _cutFacade.ProcessBatchCutsAsync(Arg.Any<List<CutOperation>>())
            .Returns(ci => { _calls.Add("cut"); return ci.Arg<List<CutOperation>>().Select(_ => new Shift()).ToList(); });
        var clock = Substitute.For<ICompanyClock>();
        clock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc));
        _executor = new OrderReviewExecutor(_shiftRepository, _unitOfWork, _sealing, _cutPlanner, _cutFacade, clock);
    }

    private static OrderReviewExecutionTarget Target(List<OrderReviewPart>? parts)
    {
        var order = new Shift { Id = Guid.NewGuid(), Name = "Nachtwache", Status = ShiftStatus.OriginalOrder };
        var item = new OrderReviewDraftItem
        {
            OrderId = order.Id,
            OrderName = order.Name,
            IsFullDay = true,
            CurrentStart = TimeOnly.MinValue,
            CurrentEnd = TimeOnly.MinValue,
            ChosenStart = Seven,
            ChosenParts = parts,
            ChosenGroupId = GroupId,
            ChosenQualificationIds = [QualificationId],
            ExpenseStatus = OrderReviewExpenseStatus.Provided,
            Expenses = [new OrderReviewExpense { Description = "Mahlzeit", Amount = 15m }],
        };
        return new OrderReviewExecutionTarget(item, order);
    }

    private void CutPlannerPlans(IEnumerable<OrderReviewPart> parts)
    {
        var planned = parts.Select((part, index) =>
            new ShiftTimeCutPart(Guid.NewGuid(), index, string.Empty, part.Start, part.End, 8m, part.End <= part.Start, index == 0)).ToList();
        _cutPlanner.PlanAsync(PlannableShiftId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ShiftTimeCutPlan(null, new Shift { Id = PlannableShiftId }, Guid.NewGuid(),
                planned.Select(_ => new CutOperation()).ToList(), planned, new List<SimpleGroupResource>(), new List<ShiftExpenses>()));
    }

    [Test]
    public async Task ChangesSavesSealsAndCuts_InThatOrder()
    {
        var parts = FullDayOrderRule.EqualParts(Seven, 3);
        var target = Target(parts);
        CutPlannerPlans(parts);

        var outcome = await _executor.ExecuteAsync(target, "admin", "de");

        Assert.That(outcome.Succeeded, Is.True, outcome.FailureReason);
        Assert.That(outcome.PartCount, Is.EqualTo(3));
        Assert.That(_calls, Is.EqualTo(new[] { "put", "save", "seal", "cut" }));
        await _shiftRepository.Received(1).Put(Arg.Is<Shift>(s =>
            s.StartShift == Seven && s.EndShift == Seven
            && s.GroupItems.Any(g => g.GroupId == GroupId)
            && s.RequiredQualifications.Any(q => q.QualificationId == QualificationId && q.IsMandatory)
            && s.ShiftExpenses.Any(e => e.Description == "Mahlzeit" && e.Amount == 15m)));
        await _cutPlanner.Received(1).PlanAsync(
            PlannableShiftId, "07:00-15:00,15:00-23:00,23:00-07:00", Arg.Any<string>(), "de", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ACutPlannedDifferently_FailsTheOrder_AndClearsTheTracker()
    {
        var parts = FullDayOrderRule.EqualParts(Seven, 3);
        CutPlannerPlans(FullDayOrderRule.EqualParts(Seven, 2));

        var outcome = await _executor.ExecuteAsync(Target(parts), "admin", "de");

        Assert.That(outcome.Succeeded, Is.False);
        Assert.That(outcome.FailureReason, Does.Contain("decided parts"));
        _unitOfWork.Received(1).DiscardTrackedChanges();
        await _cutFacade.DidNotReceiveWithAnyArgs().ProcessBatchCutsAsync(default!);
    }

    [Test]
    public async Task ARefusedSealing_FailsTheOrder()
    {
        _sealing.CheckAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new OrderSealingCheck(null, new OrderSealingOutcome(Guid.Empty, "Nachtwache", false, null, "missing: name", [])));

        var outcome = await _executor.ExecuteAsync(Target([]), "admin", "de");

        Assert.That(outcome.Succeeded, Is.False);
        Assert.That(outcome.FailureReason, Does.Contain("missing: name"));
    }

    [Test]
    public async Task WithoutASplit_NothingIsCut()
    {
        var outcome = await _executor.ExecuteAsync(Target([]), "admin", "de");

        Assert.That(outcome.Succeeded, Is.True, outcome.FailureReason);
        Assert.That(_calls, Is.EqualTo(new[] { "put", "save", "seal" }));
    }
}
