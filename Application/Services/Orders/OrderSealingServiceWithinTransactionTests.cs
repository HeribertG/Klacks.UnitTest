// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for OrderSealingService.SealWithinTransactionAsync, the sealing transition without a transaction of its
/// own: it never opens one (the caller's transaction spans change, seal and cut), returns the confirmed plannable
/// shift id and throws SkillVerificationException when the order vanished or a re-read does not confirm the write, so
/// the caller's transaction rolls back. SealAsync keeps wrapping the same core in its own transaction.
/// </summary>

using Klacks.Api.Application.Services.Orders;
using Klacks.Api.Domain.Exceptions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Services.Orders;

[TestFixture]
public class OrderSealingServiceWithinTransactionTests
{
    private static readonly Guid OrderId = Guid.NewGuid();
    private static readonly Guid PlannableShiftId = Guid.NewGuid();

    private IShiftRepository _shiftRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private OrderSealingService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _service = new OrderSealingService(_shiftRepository, _unitOfWork, ShiftVisibilityGuards.AllowAll());
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<Guid>>>())
            .Returns(ci => ci.Arg<Func<Task<Guid>>>()());
    }

    private static Shift Order() => new()
    {
        Id = OrderId,
        Status = ShiftStatus.OriginalOrder,
        Name = "Nachtwache Bern",
        Abbreviation = "NW",
        FromDate = new DateOnly(2026, 11, 2),
        IsMonday = true,
        Quantity = 1,
        SumEmployees = 1,
        GroupItems = [new GroupItem { Id = Guid.NewGuid(), GroupId = Guid.NewGuid() }]
    };

    private void PersistenceConfirms()
    {
        _shiftRepository.PutWithSealedOrderHandling(Arg.Any<Shift>())
            .Returns(new Shift { Id = PlannableShiftId, OriginalId = OrderId, Status = ShiftStatus.OriginalShift });
        _shiftRepository.GetNoTracking(OrderId).Returns(new Shift { Id = OrderId, Status = ShiftStatus.SealedOrder });
        _shiftRepository.GetNoTracking(PlannableShiftId)
            .Returns(new Shift { Id = PlannableShiftId, OriginalId = OrderId, Status = ShiftStatus.OriginalShift });
    }

    [Test]
    public async Task SealWithinTransaction_ReturnsThePlannableShift_WithoutOpeningATransaction()
    {
        PersistenceConfirms();

        var plannableShiftId = await _service.SealWithinTransactionAsync(Order());

        Assert.That(plannableShiftId, Is.EqualTo(PlannableShiftId));
        await _shiftRepository.Received(1).PutWithSealedOrderHandling(
            Arg.Is<Shift>(s => s.Id == OrderId && s.Status == ShiftStatus.SealedOrder));
        await _unitOfWork.Received(1).CompleteAsync();
        await _unitOfWork.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<Task<Guid>>>());
    }

    [Test]
    public void SealWithinTransaction_Throws_WhenThePlannableShiftIsNotConfirmed()
    {
        PersistenceConfirms();
        _shiftRepository.GetNoTracking(PlannableShiftId).Returns((Shift?)null);

        Assert.ThrowsAsync<SkillVerificationException>(() => _service.SealWithinTransactionAsync(Order()));
    }

    [Test]
    public void SealWithinTransaction_Throws_WhenTheOrderVanished()
    {
        _shiftRepository.PutWithSealedOrderHandling(Arg.Any<Shift>()).Returns((Shift?)null);

        Assert.ThrowsAsync<SkillVerificationException>(() => _service.SealWithinTransactionAsync(Order()));
    }

    [Test]
    public async Task SealAsync_StillRunsTheTransitionInItsOwnTransaction()
    {
        _shiftRepository.Get(OrderId).Returns(Order());
        PersistenceConfirms();

        var outcome = await _service.SealAsync(OrderId);

        Assert.That(outcome.IsSealed, Is.True);
        Assert.That(outcome.PlannableShiftId, Is.EqualTo(PlannableShiftId));
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<Task<Guid>>>());
    }
}
