// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for SealOpenOrdersCommandHandler running against the real OrderSealingService, so the
/// sealing requirements and the transition itself are the ones seal_shift uses: a preview classifies
/// sealable and blocked orders without writing, an apply seals every sealable order and keeps going when
/// one of them throws, a refusal is recorded against its own order, and autoAssignGroups sends the
/// assignment command before the sealing pass instead of duplicating its logic. With a group scope only orders
/// linked to at least one visible group are considered at all (never orders without a group), maxCount is
/// applied after that filter, preview and apply cover the identical set, and the group assignment is refused.
/// </summary>

using Klacks.UnitTest.TestHelpers;
using Klacks.Api.Application.Commands.Orders;
using Klacks.Api.Application.DTOs.Orders;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Orders;
using Klacks.Api.Application.Services.Orders;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Handlers.Orders;

[TestFixture]
public class SealOpenOrdersCommandHandlerTests
{
    private IShiftRepository _shiftRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IMediator _mediator = null!;
    private OrderSealingService _orderSealingService = null!;
    private SealOpenOrdersCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _mediator = Substitute.For<IMediator>();
        _orderSealingService = new OrderSealingService(_shiftRepository, _unitOfWork, ShiftVisibilityGuards.AllowAll());
        _handler = new SealOpenOrdersCommandHandler(_shiftRepository, _orderSealingService, _mediator);

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<Guid>>>())
            .Returns(ci => ci.Arg<Func<Task<Guid>>>()());
    }

    private static SealOpenOrdersCommand Command(
        bool apply, bool autoAssignGroups = false, OpenOrderSealingScope? scope = null, int? maxCount = null) =>
        new(SourceSystemId: null, FromDate: null, UntilDate: null, CustomerName: null, GroupId: null,
            maxCount, autoAssignGroups, ValidFrom: null, apply, "tester", scope ?? OpenOrderSealingScope.FullAccess());

    private static Shift OrderInGroups(string name, params Guid[] groupIds)
    {
        var order = SealableOrder(name);
        order.GroupItems = groupIds.Select(id => new GroupItem { Id = Guid.NewGuid(), GroupId = id }).ToList();
        return order;
    }

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

    private static Shift OrderWithoutGroup(string name)
    {
        var order = SealableOrder(name);
        order.GroupItems = new List<GroupItem>();
        return order;
    }

    private void OpenOrders(params Shift[] orders) =>
        _shiftRepository.GetOpenOrdersAsync(Arg.Any<OpenOrderFilter>(), Arg.Any<CancellationToken>())
            .Returns(orders.ToList());

    private void MakeSealable(Shift order)
    {
        var plannableShiftId = Guid.NewGuid();
        _shiftRepository.Get(order.Id).Returns(order);
        _shiftRepository.PutWithSealedOrderHandling(Arg.Is<Shift>(s => s.Id == order.Id))
            .Returns(new Shift { Id = plannableShiftId, OriginalId = order.Id, Status = ShiftStatus.OriginalShift });
        _shiftRepository.GetNoTracking(order.Id)
            .Returns(new Shift { Id = order.Id, Status = ShiftStatus.SealedOrder });
        _shiftRepository.GetNoTracking(plannableShiftId)
            .Returns(new Shift { Id = plannableShiftId, OriginalId = order.Id, Status = ShiftStatus.OriginalShift });
    }

    [Test]
    public async Task Preview_SplitsSealableFromBlocked_AndWritesNothing()
    {
        OpenOrders(SealableOrder("with group"), OrderWithoutGroup("without group"));

        var result = await _handler.Handle(Command(apply: false), CancellationToken.None);

        result.Applied.ShouldBeFalse();
        result.TotalOrders.ShouldBe(2);
        result.SealableCount.ShouldBe(1);
        result.BlockedCount.ShouldBe(1);
        result.SealedCount.ShouldBe(0);
        result.BlockedSample[0].MissingRequirements.ShouldContain("at least one group");
        result.BlockedOnlyByMissingGroupCount.ShouldBe(1);
        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task Apply_SealsEverySealableOrder_AndReportsThePlannableShifts()
    {
        var first = SealableOrder("first");
        var second = SealableOrder("second");
        var third = SealableOrder("third");
        OpenOrders(first, second, third);
        MakeSealable(first);
        MakeSealable(second);
        MakeSealable(third);

        var result = await _handler.Handle(Command(apply: true), CancellationToken.None);

        result.SealedCount.ShouldBe(3);
        result.FailedCount.ShouldBe(0);
        result.SealedSample.Select(s => s.PlannableShiftId).Distinct().Count().ShouldBe(3);
        result.SealedSample.ShouldAllBe(s => s.PlannableShiftId != Guid.Empty);
        await _unitOfWork.Received(3).CompleteAsync();
    }

    [Test]
    public async Task Apply_KeepsGoing_WhenOneOrderThrows()
    {
        var first = SealableOrder("first");
        var second = SealableOrder("second");
        var third = SealableOrder("third");
        OpenOrders(first, second, third);
        MakeSealable(first);
        MakeSealable(third);
        _shiftRepository.Get(second.Id).Returns(second);
        _shiftRepository.PutWithSealedOrderHandling(Arg.Is<Shift>(s => s.Id == second.Id))
            .Returns<Shift?>(_ => throw new InvalidOperationException("database exploded"));

        var result = await _handler.Handle(Command(apply: true), CancellationToken.None);

        result.SealedCount.ShouldBe(2);
        result.FailedCount.ShouldBe(1);
        result.Failures[0].OrderName.ShouldBe("second");
        result.Failures[0].Reason.ShouldContain("database exploded");
        result.SealedSample.Select(s => s.OrderName).ShouldBe(new[] { "first", "third" });
    }

    [Test]
    public async Task Apply_RecordsARefusalAsAFailure_WhenTheWriteCannotBeConfirmed()
    {
        var order = SealableOrder("unconfirmable");
        OpenOrders(order);
        var plannableShiftId = Guid.NewGuid();
        _shiftRepository.Get(order.Id).Returns(order);
        _shiftRepository.PutWithSealedOrderHandling(Arg.Any<Shift>())
            .Returns(new Shift { Id = plannableShiftId, OriginalId = order.Id, Status = ShiftStatus.OriginalShift });
        _shiftRepository.GetNoTracking(order.Id)
            .Returns(new Shift { Id = order.Id, Status = ShiftStatus.SealedOrder });
        _shiftRepository.GetNoTracking(plannableShiftId).Returns((Shift?)null);

        var result = await _handler.Handle(Command(apply: true), CancellationToken.None);

        result.SealedCount.ShouldBe(0);
        result.FailedCount.ShouldBe(1);
        result.Failures[0].Reason.ShouldContain("rolled back");
    }

    [Test]
    public async Task AutoAssignGroups_SendsTheAssignmentCommandBeforeSealing()
    {
        var order = SealableOrder("with group");
        OpenOrders(order);
        MakeSealable(order);
        _mediator.Send(Arg.Any<AssignOrdersToGroupsCommand>(), Arg.Any<CancellationToken>())
            .Returns(new AssignOrdersToGroupsResult(
                true, 1, 0, 4, 4, 0, [], [], []));

        var result = await _handler.Handle(Command(apply: true, autoAssignGroups: true), CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<AssignOrdersToGroupsCommand>(c => c.Apply), Arg.Any<CancellationToken>());
        result.AutoAssignRequested.ShouldBeTrue();
        result.AutoAssignedCount.ShouldBe(4);
        result.SealedCount.ShouldBe(1);
    }

    [Test]
    public async Task BlockedOnlyByMissingGroup_ExcludesOrdersMissingSomethingElseToo()
    {
        var alsoMissingName = OrderWithoutGroup("nameless");
        alsoMissingName.Name = string.Empty;
        OpenOrders(OrderWithoutGroup("only the group"), alsoMissingName);

        var result = await _handler.Handle(Command(apply: false), CancellationToken.None);

        result.BlockedCount.ShouldBe(2);
        result.BlockedOnlyByMissingGroupCount.ShouldBe(1);
    }

    [Test]
    public async Task WithoutAutoAssignGroups_NoAssignmentCommandIsSent()
    {
        OpenOrders(OrderWithoutGroup("without group"));

        var result = await _handler.Handle(Command(apply: false), CancellationToken.None);

        await _mediator.DidNotReceive().Send(
            Arg.Any<AssignOrdersToGroupsCommand>(), Arg.Any<CancellationToken>());
        result.AutoAssignRequested.ShouldBeFalse();
        result.AutoAssignedCount.ShouldBe(0);
    }

    [Test]
    public async Task FullAccess_IncludesOrdersWithoutAGroup()
    {
        OpenOrders(SealableOrder("with group"), OrderWithoutGroup("without group"));

        var result = await _handler.Handle(Command(apply: false), CancellationToken.None);

        result.TotalOrders.ShouldBe(2);
        result.BlockedSample.Select(b => b.OrderName).ShouldContain("without group");
    }

    [Test]
    public async Task GroupScope_ConsidersOnlyOrdersInVisibleGroups_AndNeverOrdersWithoutAGroup()
    {
        var visibleGroupId = Guid.NewGuid();
        var hiddenGroupId = Guid.NewGuid();
        OpenOrders(
            OrderInGroups("visible", visibleGroupId),
            OrderInGroups("hidden", hiddenGroupId),
            OrderWithoutGroup("ungrouped"));

        var result = await _handler.Handle(
            Command(apply: false, scope: OpenOrderSealingScope.ForGroups([visibleGroupId])), CancellationToken.None);

        result.TotalOrders.ShouldBe(1);
        result.SealableCount.ShouldBe(1);
        result.BlockedCount.ShouldBe(0);
        result.BlockedOnlyByMissingGroupCount.ShouldBe(0);
    }

    [Test]
    public async Task GroupScope_IncludesAnOrderLinkedToAtLeastOneVisibleGroup_LikeSealShift()
    {
        var visibleGroupId = Guid.NewGuid();
        var mixed = OrderInGroups("mixed", visibleGroupId, Guid.NewGuid());
        OpenOrders(mixed);
        MakeSealable(mixed);

        var result = await _handler.Handle(
            Command(apply: true, scope: OpenOrderSealingScope.ForGroups([visibleGroupId])), CancellationToken.None);

        result.SealedCount.ShouldBe(1);
    }

    [Test]
    public async Task GroupScope_IgnoresADeletedLinkToAVisibleGroup()
    {
        var visibleGroupId = Guid.NewGuid();
        var order = OrderInGroups("deleted link", visibleGroupId, Guid.NewGuid());
        order.GroupItems.First(gi => gi.GroupId == visibleGroupId).IsDeleted = true;
        OpenOrders(order);

        var result = await _handler.Handle(
            Command(apply: false, scope: OpenOrderSealingScope.ForGroups([visibleGroupId])), CancellationToken.None);

        result.TotalOrders.ShouldBe(0);
    }

    [Test]
    public async Task GroupScope_AppliesMaxCountAfterTheScopeFilter()
    {
        var visibleGroupId = Guid.NewGuid();
        OpenOrders(
            OrderInGroups("hidden 1", Guid.NewGuid()),
            OrderInGroups("hidden 2", Guid.NewGuid()),
            OrderInGroups("visible 1", visibleGroupId),
            OrderInGroups("visible 2", visibleGroupId),
            OrderInGroups("visible 3", visibleGroupId));

        var result = await _handler.Handle(
            Command(apply: false, scope: OpenOrderSealingScope.ForGroups([visibleGroupId]), maxCount: 2),
            CancellationToken.None);

        result.TotalOrders.ShouldBe(2);
        await _shiftRepository.Received(1).GetOpenOrdersAsync(
            Arg.Is<OpenOrderFilter>(f => f.MaxCount == null), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FullAccess_PassesMaxCountToTheRepository()
    {
        OpenOrders(SealableOrder("one"));

        await _handler.Handle(Command(apply: false, maxCount: 7), CancellationToken.None);

        await _shiftRepository.Received(1).GetOpenOrdersAsync(
            Arg.Is<OpenOrderFilter>(f => f.MaxCount == 7), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GroupScope_PreviewAndApplyCoverTheIdenticalSet_AndHiddenOrdersAreNeverWritten()
    {
        var visibleGroupId = Guid.NewGuid();
        var visibleA = OrderInGroups("visible a", visibleGroupId);
        var visibleB = OrderInGroups("visible b", visibleGroupId);
        var hidden = OrderInGroups("hidden", Guid.NewGuid());
        var ungrouped = OrderWithoutGroup("ungrouped");
        OpenOrders(visibleA, hidden, visibleB, ungrouped);
        MakeSealable(visibleA);
        MakeSealable(visibleB);
        MakeSealable(hidden);
        var scope = OpenOrderSealingScope.ForGroups([visibleGroupId]);

        var preview = await _handler.Handle(Command(apply: false, scope: scope), CancellationToken.None);
        var applied = await _handler.Handle(Command(apply: true, scope: scope), CancellationToken.None);

        applied.TotalOrders.ShouldBe(preview.TotalOrders);
        applied.SealedCount.ShouldBe(preview.SealableCount);
        applied.SealedSample.Select(s => s.OrderId).ShouldBe(new[] { visibleA.Id, visibleB.Id }, ignoreOrder: true);
        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Is<Shift>(s => s.Id == hidden.Id));
        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Is<Shift>(s => s.Id == ungrouped.Id));
    }

    [Test]
    public async Task GroupScope_RefusesTheGroupAssignment_WithAForbiddenRejection()
    {
        OpenOrders(OrderWithoutGroup("ungrouped"));

        await Should.ThrowAsync<ForbiddenException>(() => _handler.Handle(
            Command(apply: true, autoAssignGroups: true, scope: OpenOrderSealingScope.ForGroups([Guid.NewGuid()])),
            CancellationToken.None));

        await _mediator.DidNotReceive().Send(
            Arg.Any<AssignOrdersToGroupsCommand>(), Arg.Any<CancellationToken>());
        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Any<Shift>());
    }
}
