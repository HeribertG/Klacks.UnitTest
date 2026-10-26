// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// SealOpenOrdersCommandHandler with the confirmed preview on the command (ExpectedSelection), running against the real
/// OrderSealingService: a preview reports every matched order with its classification; an apply whose orders no longer
/// classify exactly as confirmed throws before the group assignment or any seal is written; an apply that matches seals
/// only confirmed orders, even when another order shows up between the check and the sealing pass (the background job
/// runs the same handler later); without an expected selection nothing is checked, as before.
/// </summary>

using Klacks.Api.Application.Commands.Orders;
using Klacks.Api.Application.DTOs.Orders;
using Klacks.Api.Application.Handlers.Orders;
using Klacks.Api.Application.Services.Orders;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Handlers.Orders;

[TestFixture]
public class SealOpenOrdersConfirmedSelectionTests
{
    private IShiftRepository _shiftRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IMediator _mediator = null!;
    private SealOpenOrdersCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<AssignOrdersToGroupsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => new AssignOrdersToGroupsResult(
                Applied: call.Arg<AssignOrdersToGroupsCommand>().Apply, TotalOrders: 0, SkippedAlreadyGroupedCount: 0,
                AssignedCount: 0, VerifiedCount: 0, UnassignableCount: 0, Targets: [], AssignmentSample: [],
                UnassignableSample: []));
        var orderSealingService = new OrderSealingService(_shiftRepository, _unitOfWork, ShiftVisibilityGuards.AllowAll());
        _handler = new SealOpenOrdersCommandHandler(_shiftRepository, orderSealingService, _mediator);

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<Guid>>>())
            .Returns(ci => ci.Arg<Func<Task<Guid>>>()());
    }

    private static SealOpenOrdersCommand Command(
        bool apply, OpenOrderSealingSelection? expected = null, bool autoAssignGroups = false) =>
        new(SourceSystemId: null, FromDate: null, UntilDate: null, CustomerName: null, GroupId: null,
            MaxCount: null, autoAssignGroups, ValidFrom: null, apply, "tester", OpenOrderSealingScope.FullAccess(), expected);

    private static Shift SealableOrder(string name) => new()
    {
        Id = Guid.NewGuid(),
        Status = ShiftStatus.OriginalOrder,
        Name = name,
        Abbreviation = "FS",
        FromDate = new DateOnly(2026, 6, 1),
        IsMonday = true,
        Quantity = 1,
        SumEmployees = 2,
        GroupItems = new List<GroupItem> { new() { Id = Guid.NewGuid(), GroupId = Guid.NewGuid() } }
    };

    private static Shift BlockedOrder(string name)
    {
        var order = SealableOrder(name);
        order.GroupItems = new List<GroupItem>();
        return order;
    }

    private void MakeSealable(Shift order)
    {
        var plannableShiftId = Guid.NewGuid();
        _shiftRepository.Get(order.Id).Returns(order);
        _shiftRepository.PutWithSealedOrderHandling(Arg.Is<Shift>(s => s.Id == order.Id))
            .Returns(new Shift { Id = plannableShiftId, OriginalId = order.Id, Status = ShiftStatus.OriginalShift });
        _shiftRepository.GetNoTracking(order.Id).Returns(new Shift { Id = order.Id, Status = ShiftStatus.SealedOrder });
        _shiftRepository.GetNoTracking(plannableShiftId)
            .Returns(new Shift { Id = plannableShiftId, OriginalId = order.Id, Status = ShiftStatus.OriginalShift });
    }

    [Test]
    public async Task Preview_ReportsEveryMatchedOrderWithItsClassification()
    {
        var sealable = SealableOrder("sealable");
        var blocked = BlockedOrder("blocked");
        _shiftRepository.GetOpenOrdersAsync(Arg.Any<OpenOrderFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<Shift> { sealable, blocked });

        var result = await _handler.Handle(Command(apply: false), CancellationToken.None);

        result.Selection.SealableIds.ShouldBe([sealable.Id]);
        result.Selection.BlockedIds.ShouldBe([blocked.Id]);
    }

    [Test]
    public async Task Apply_WhenAnOrderAppearedSinceTheConfirmation_ThrowsBeforeAnythingIsWritten()
    {
        var shown = SealableOrder("shown");
        var appeared = SealableOrder("appeared");
        MakeSealable(shown);
        MakeSealable(appeared);
        _shiftRepository.GetOpenOrdersAsync(Arg.Any<OpenOrderFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<Shift> { shown, appeared });
        var confirmed = OpenOrderSealingSelection.Of([shown.Id], []);

        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(Command(apply: true, confirmed, autoAssignGroups: true), CancellationToken.None));

        await _mediator.DidNotReceive().Send(Arg.Any<AssignOrdersToGroupsCommand>(), Arg.Any<CancellationToken>());
        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task Apply_WhenAConfirmedBlockedOrderBecameSealable_ThrowsAndSealsNothing()
    {
        var order = SealableOrder("fixed in the meantime");
        MakeSealable(order);
        _shiftRepository.GetOpenOrdersAsync(Arg.Any<OpenOrderFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<Shift> { order });

        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(Command(apply: true, OpenOrderSealingSelection.Of([], [order.Id])), CancellationToken.None));

        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task Apply_MatchingTheConfirmation_SealsOnlyConfirmedOrders_EvenWhenOneAppearsAfterTheCheck()
    {
        var shown = SealableOrder("shown");
        var late = SealableOrder("late");
        MakeSealable(shown);
        MakeSealable(late);
        _shiftRepository.GetOpenOrdersAsync(Arg.Any<OpenOrderFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<Shift> { shown }, new List<Shift> { shown, late });

        var result = await _handler.Handle(
            Command(apply: true, OpenOrderSealingSelection.Of([shown.Id], [])), CancellationToken.None);

        result.SealedCount.ShouldBe(1);
        await _shiftRepository.Received(1).PutWithSealedOrderHandling(Arg.Is<Shift>(s => s.Id == shown.Id));
        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Is<Shift>(s => s.Id == late.Id));
    }

    [Test]
    public async Task Apply_WithoutAnExpectedSelection_ChecksNothing()
    {
        var order = SealableOrder("order");
        MakeSealable(order);
        _shiftRepository.GetOpenOrdersAsync(Arg.Any<OpenOrderFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<Shift> { order });

        var result = await _handler.Handle(Command(apply: true), CancellationToken.None);

        result.SealedCount.ShouldBe(1);
        await _shiftRepository.Received(1).GetOpenOrdersAsync(Arg.Any<OpenOrderFilter>(), Arg.Any<CancellationToken>());
    }
}
