// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ShiftFamilyReset: the lock comes before the first read, every refusal comes before any write, a caller
/// who does not see the whole family gets the answer of a missing order, and a removed row is deleted before its tree is
/// numbered in a second save.
/// </summary>

using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Shifts;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class ShiftFamilyResetTests
{
    private static readonly DateOnly OrderFrom = new(2026, 1, 1);
    private static readonly DateOnly Day = new(2026, 4, 1);

    private readonly Shift _order = new() { Id = Guid.NewGuid(), Status = ShiftStatus.SealedOrder, FromDate = OrderFrom };

    private IShiftRepository _shiftRepository = null!;
    private IOrderFamilyEndRepository _familyRepository = null!;
    private IShiftExpensesRepository _expenses = null!;
    private IShiftResetService _resetService = null!;
    private IShiftTreeService _treeService = null!;
    private IShiftVisibilityGuard _visibility = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ShiftFamilyReset _reset = null!;

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _familyRepository = Substitute.For<IOrderFamilyEndRepository>();
        _expenses = Substitute.For<IShiftExpensesRepository>();
        _resetService = Substitute.For<IShiftResetService>();
        _treeService = Substitute.For<IShiftTreeService>();
        _visibility = ShiftVisibilityGuards.AllowAll();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<List<Shift>>>>())
            .Returns(call => call.Arg<Func<Task<List<Shift>>>>()());
        _shiftRepository.GetSealedOrder(_order.Id, null).Returns(_order);
        _expenses.GetByShiftIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<ShiftExpenses>());
        _familyRepository.FindShiftIdsWithRealWorkAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());
        _familyRepository.GetUsageAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => new OrderFamilyUsage(call.Arg<IReadOnlyList<Guid>>(), false, false, false));

        _reset = new ShiftFamilyReset(
            _shiftRepository, _familyRepository, _expenses, _resetService, _treeService, _visibility, _unitOfWork,
            Substitute.For<ILogger<ShiftFamilyReset>>());
    }

    private Shift Piece(DateOnly from, DateOnly? until = null, Guid? rootId = null) => new()
    {
        Id = Guid.NewGuid(),
        OriginalId = _order.Id,
        Status = ShiftStatus.SplitShift,
        FromDate = from,
        UntilDate = until,
        RootId = rootId
    };

    private void Family(params Shift[] rows) =>
        _shiftRepository.CutList(_order.Id, null, true, null).Returns(rows.ToList());

    private void WorkOn(params Guid[] ids) =>
        _familyRepository.FindShiftIdsWithRealWorkAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ids.ToHashSet());

    private void LastWork(DateOnly day) =>
        _familyRepository.FindLastWorkDateAfterAsync(_order.Id, DateOnly.MinValue, Arg.Any<CancellationToken>()).Returns(day);

    private async Task AssertNothingWrittenAsync()
    {
        await _resetService.DidNotReceiveWithAnyArgs().CreateNewOriginalShiftFromSealedOrderAsync(default!, default, default!);
        await _shiftRepository.DidNotReceiveWithAnyArgs().DeleteOrderFamilyAsync(default!, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    private async Task<ShiftResetRefusedException> RefusedAsync(string reason)
    {
        var thrown = await Should.ThrowAsync<ShiftResetRefusedException>(() => _reset.ResetAsync(_order, Day));

        thrown.ConflictCode.ShouldBe(ShiftResetRefusedException.ErrorCode);
        thrown.ConflictDetails![ShiftResetRefusedException.ReasonKey].ShouldBe(reason);
        await AssertNothingWrittenAsync();
        return thrown;
    }

    [Test]
    public async Task Reset_LocksTheHeadBeforeItReads_AndDiscardsTheTrackerFirst()
    {
        Family(Piece(OrderFrom));

        await _reset.ResetAsync(_order, Day);

        Received.InOrder(() =>
        {
            _unitOfWork.DiscardTrackedChanges();
            _familyRepository.LockHeadAsync(_order.Id, Arg.Any<CancellationToken>());
            _shiftRepository.CutList(_order.Id, null, true, null);
        });
    }

    [Test]
    public async Task Reset_WithAHiddenRow_IsAnsweredLikeAMissingOrder_BeforeAnyWorkDateIsRead()
    {
        var hidden = Piece(OrderFrom);
        Family(hidden);
        _visibility.FindFirstHiddenAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(hidden.Id);

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() => _reset.ResetAsync(_order, Day));

        thrown.Message.ShouldBe(string.Format(ShiftVisibilityMessages.NoSealedOrderFound, _order.Id));
        await _familyRepository.DidNotReceiveWithAnyArgs().FindLastWorkDateAfterAsync(default, default, default);
        await AssertNothingWrittenAsync();
    }

    [Test]
    public async Task Reset_WithoutRows_ReturnsEmpty_AndWritesNothing()
    {
        Family();

        (await _reset.ResetAsync(_order, Day)).ShouldBeEmpty();

        await AssertNothingWrittenAsync();
    }

    [Test]
    public async Task Reset_ReadsTheSealedRowAfterTheLock_SoAnEndSetMeanwhileCounts()
    {
        Family(Piece(OrderFrom));

        await _reset.ResetAsync(_order, Day);

        Received.InOrder(() =>
        {
            _familyRepository.LockHeadAsync(_order.Id, Arg.Any<CancellationToken>());
            _shiftRepository.GetSealedOrder(_order.Id, null);
            _shiftRepository.CutList(_order.Id, null, true, null);
        });
    }

    [Test]
    public async Task Reset_WhenTheSealedRowIsGoneAfterTheLock_IsAnsweredLikeAMissingOrder()
    {
        _shiftRepository.GetSealedOrder(_order.Id, null).Returns((Shift?)null);

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() => _reset.ResetAsync(_order, Day));

        thrown.Message.ShouldBe(string.Format(ShiftVisibilityMessages.NoSealedOrderFound, _order.Id));
        await AssertNothingWrittenAsync();
    }

    [Test]
    public async Task Reset_BehindTheEndOfTheOrder_IsRefused_AndWritesNothing()
    {
        var endedOrder = new Shift { Id = _order.Id, Status = ShiftStatus.SealedOrder, FromDate = OrderFrom, UntilDate = Day.AddDays(-1) };
        _shiftRepository.GetSealedOrder(_order.Id, null).Returns(endedOrder);
        Family(Piece(OrderFrom));
        LastWork(Day.AddDays(-2));

        var thrown = await Should.ThrowAsync<BadRequestException>(() => _reset.ResetAsync(_order, Day));

        thrown.Message.ShouldBe("A reset from 2026-04-01 is not inside the shift, which ends on 2026-03-31.");
        await AssertNothingWrittenAsync();
    }

    [Test]
    public async Task Reset_WithAWorkOnTheDay_IsRefusedWithTheDate()
    {
        Family(Piece(OrderFrom));
        LastWork(Day);

        var thrown = await RefusedAsync(ShiftResetRefusedException.WorksOnOrAfterReason);

        thrown.ConflictDetails![ShiftResetRefusedException.LastWorkDateKey].ShouldBe("2026-04-01");
    }

    [Test]
    public async Task Reset_WithAScenarioWorkFromTheStart_IsRefusedWithoutAWorkDate()
    {
        Family(Piece(OrderFrom));
        _familyRepository.FindLastScenarioWorkDateAfterAsync(_order.Id, OrderFrom.AddDays(-1), Arg.Any<CancellationToken>())
            .Returns(Day);

        var thrown = await RefusedAsync(ShiftResetRefusedException.ScenarioWorksOnOrAfterReason);

        thrown.ConflictDetails!.ContainsKey(ShiftResetRefusedException.LastWorkDateKey).ShouldBeFalse();
    }

    [Test]
    public async Task Reset_WithWork_LooksForScenarioWorksFromTheRequestedDay()
    {
        var worked = Piece(OrderFrom);
        Family(worked);
        LastWork(Day.AddDays(-1));
        WorkOn(worked.Id);

        await _reset.ResetAsync(_order, Day);

        await _familyRepository.Received(1).FindLastScenarioWorkDateAfterAsync(_order.Id, Day.AddDays(-1), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Reset_OfAPlannableShiftWithoutOpenPiece_IsRefused()
    {
        Family(new Shift { Id = Guid.NewGuid(), OriginalId = _order.Id, Status = ShiftStatus.OriginalShift, FromDate = OrderFrom });

        await RefusedAsync(ShiftResetRefusedException.NothingToResetReason);
    }

    [Test]
    public async Task Reset_WithAKeptRowFromTheStart_IsRefused()
    {
        var worked = Piece(Day);
        Family(Piece(OrderFrom), worked);
        LastWork(Day.AddDays(-1));
        WorkOn(worked.Id);

        await RefusedAsync(ShiftResetRefusedException.RowsInUseReason);
    }

    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(false, false, true)]
    public async Task Reset_WithARowToRemoveInUse_IsRefusedWithoutADate(bool works, bool clones, bool containers)
    {
        Family(Piece(OrderFrom));
        _familyRepository.GetUsageAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => new OrderFamilyUsage(call.Arg<IReadOnlyList<Guid>>(), works, clones, containers));

        var thrown = await RefusedAsync(ShiftResetRefusedException.RowsInUseReason);

        thrown.ConflictDetails!.Keys.ShouldBe([ShiftResetRefusedException.ReasonKey]);
    }

    [Test]
    public async Task Reset_AsksTheUsageRuleOnlyForTheRowsToRemove()
    {
        var worked = Piece(OrderFrom);
        var later = Piece(Day.AddDays(10));
        Family(worked, later);
        LastWork(Day.AddDays(-1));
        WorkOn(worked.Id);

        await _reset.ResetAsync(_order, Day);

        await _familyRepository.Received(1).GetUsageAsync(
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == later.Id), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Reset_WithoutWork_StartsOnTheOrderStart_AndCopiesTheExpensesOfTheOrder()
    {
        var stored = new List<ShiftExpenses> { new() { ShiftId = _order.Id, Amount = 3m } };
        _expenses.GetByShiftIdsAsync(Arg.Is<IEnumerable<Guid>>(ids => ids.Single() == _order.Id), Arg.Any<CancellationToken>()).Returns(stored);
        Family(Piece(OrderFrom));

        await _reset.ResetAsync(_order, Day);

        await _resetService.Received(1).CreateNewOriginalShiftFromSealedOrderAsync(_order, OrderFrom, stored);
    }

    [Test]
    public async Task Reset_WithWorkBefore_ClosesTheKeptPiece_StartsOnTheDay_AndDeletesBeforeTheTreeIsNumberedInASecondSave()
    {
        var rootId = Guid.NewGuid();
        var worked = Piece(OrderFrom, rootId: rootId);
        var later = Piece(Day.AddDays(10), rootId: rootId);
        Family(worked, later);
        LastWork(Day.AddDays(-1));
        WorkOn(worked.Id);

        await _reset.ResetAsync(_order, Day);

        worked.UntilDate.ShouldBe(Day.AddDays(-1));
        await _resetService.Received(1).CreateNewOriginalShiftFromSealedOrderAsync(_order, Day, Arg.Any<IReadOnlyCollection<ShiftExpenses>>());
        Received.InOrder(() =>
        {
            _shiftRepository.DeleteOrderFamilyAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == later.Id), Arg.Any<CancellationToken>());
            _unitOfWork.CompleteAsync();
            _treeService.RecalculateTreesAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == rootId), Arg.Any<CancellationToken>());
            _unitOfWork.CompleteAsync();
        });
    }

    [Test]
    public async Task Reset_WithNothingToRemove_SavesOnce_AndNumbersNoTree()
    {
        var worked = Piece(OrderFrom);
        Family(worked);
        LastWork(Day.AddDays(-1));
        WorkOn(worked.Id);

        await _reset.ResetAsync(_order, Day);

        await _shiftRepository.DidNotReceiveWithAnyArgs().DeleteOrderFamilyAsync(default!, default);
        await _treeService.DidNotReceiveWithAnyArgs().RecalculateTreesAsync(default!, default);
        await _unitOfWork.Received(1).CompleteAsync();
    }
}
