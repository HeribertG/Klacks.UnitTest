// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ShiftValidator.ValidateCutDatesWithinAllowedRange: a part of a time cut keeps the period of its order,
/// so a part ending on the order's last day is allowed (found 2026-10-24: every time cut of an order with an end date was
/// refused because equal end dates counted as "later"); a part ending after the order, or starting before it, is refused.
/// </summary>

using Klacks.Api.Domain.Services.Shifts;

namespace Klacks.UnitTest.Services.Shifts;

[TestFixture]
public class ShiftValidatorCutDateRangeTests
{
    private static readonly Guid OrderId = Guid.NewGuid();
    private static readonly DateOnly From = new(2027, 1, 4);
    private static readonly DateOnly Until = new(2027, 1, 10);

    private IShiftRepository _shiftRepository = null!;

    [SetUp]
    public void SetUp()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftRepository.GetSealedOrder(OrderId)
            .Returns(new Shift { Id = OrderId, Status = ShiftStatus.SealedOrder, FromDate = From, UntilDate = Until });
    }

    [Test]
    public void APartWithThePeriodOfItsOrder_IsAllowed()
    {
        Assert.DoesNotThrowAsync(() =>
            new ShiftValidator().ValidateCutDatesWithinAllowedRange(OrderId, From, Until, _shiftRepository));
    }

    [Test]
    public void APartEndingAfterItsOrder_IsRefused()
    {
        Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ShiftValidator().ValidateCutDatesWithinAllowedRange(OrderId, From, Until.AddDays(1), _shiftRepository));
    }

    [Test]
    public void APartStartingBeforeItsOrder_IsRefused()
    {
        Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ShiftValidator().ValidateCutDatesWithinAllowedRange(OrderId, From.AddDays(-1), Until, _shiftRepository));
    }
}
