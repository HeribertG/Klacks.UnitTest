// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ShiftFamilyRules: which row heads a family, which rows have no order, and when a family without an order
/// counts as ended.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Shifts;

namespace Klacks.UnitTest.Domain.Services.Shifts;

[TestFixture]
public class ShiftFamilyRulesTests
{
    private static readonly Guid OrderId = Guid.NewGuid();
    private static readonly Guid RootId = Guid.NewGuid();

    [Test]
    public void HeadId_PrefersTheOrder_ThenTheRoot_ThenTheRowItself()
    {
        var row = new Shift { Id = Guid.NewGuid(), Status = ShiftStatus.SplitShift, OriginalId = OrderId, RootId = RootId };

        ShiftFamilyRules.HeadIdOf(row).ShouldBe(OrderId);
        row.OriginalId = null;
        ShiftFamilyRules.HeadIdOf(row).ShouldBe(RootId);
        row.RootId = null;
        ShiftFamilyRules.HeadIdOf(row).ShouldBe(row.Id);
    }

    [Test]
    public void HeadId_OfASealedOrderWithAForeignRoot_IsTheOrderItself()
    {
        var order = new Shift { Id = OrderId, Status = ShiftStatus.SealedOrder, RootId = RootId };

        ShiftFamilyRules.HeadIdOf(order).ShouldBe(OrderId);
    }

    [TestCase(ShiftStatus.OriginalShift, true)]
    [TestCase(ShiftStatus.SplitShift, true)]
    [TestCase(ShiftStatus.SealedOrder, false)]
    [TestCase(ShiftStatus.OriginalOrder, false)]
    public void IsWithoutOrder_DependsOnTheStatus_WhenThereIsNoOrderReference(ShiftStatus status, bool expected)
    {
        ShiftFamilyRules.IsWithoutOrder(status, null).ShouldBe(expected);
    }

    [Test]
    public void IsWithoutOrder_IsFalse_WithAnOrderReference()
    {
        ShiftFamilyRules.IsWithoutOrder(ShiftStatus.SplitShift, OrderId).ShouldBeFalse();
    }

    [Test]
    public void StoredEnd_IsEmpty_WhileARowIsOpen()
    {
        Shift[] family = [new() { UntilDate = new DateOnly(2026, 6, 30) }, new() { UntilDate = null }];

        ShiftFamilyRules.StoredEndWithoutOrder(family).ShouldBeNull();
    }

    [Test]
    public void StoredEnd_IsTheLatestEnd_WhenNoRowIsOpen()
    {
        Shift[] family = [new() { UntilDate = new DateOnly(2026, 8, 31) }, new() { UntilDate = new DateOnly(2026, 6, 30) }];

        ShiftFamilyRules.StoredEndWithoutOrder(family).ShouldBe(new DateOnly(2026, 8, 31));
    }

    [Test]
    public void StoredEnd_IsEmpty_ForAnEmptyFamily()
    {
        ShiftFamilyRules.StoredEndWithoutOrder([]).ShouldBeNull();
    }
}
