// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ShiftFamilyEndPlanner: rows without an end or with a later end get the day, a row ending earlier stays,
/// a derived row starting after the day goes with its whole subtree, and the sealed order is never removed.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Shifts;

namespace Klacks.UnitTest.Domain.Services.Shifts;

[TestFixture]
public class ShiftFamilyEndPlannerTests
{
    private static readonly Guid OrderId = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 3, 31);

    private static Shift Row(ShiftStatus status, DateOnly from, DateOnly? until = null, Guid? parentId = null) => new()
    {
        Id = status == ShiftStatus.SealedOrder ? OrderId : Guid.NewGuid(),
        OriginalId = status == ShiftStatus.SealedOrder ? null : OrderId,
        Status = status,
        FromDate = from,
        UntilDate = until,
        ParentId = parentId
    };

    [Test]
    public void TrimsOpenRowsAndLaterEnds_AndLeavesARowEndingEarlierOrOnTheDay()
    {
        var order = Row(ShiftStatus.SealedOrder, new DateOnly(2026, 1, 1));
        var open = Row(ShiftStatus.OriginalShift, new DateOnly(2026, 1, 1));
        var later = Row(ShiftStatus.SplitShift, new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30));
        var earlier = Row(ShiftStatus.SplitShift, new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 28));
        var onTheDay = Row(ShiftStatus.SplitShift, new DateOnly(2026, 1, 1), Day);

        var plan = ShiftFamilyEndPlanner.Build([order, open, later, earlier, onTheDay], OrderId, Day);

        plan.RowsToTrim.ShouldBe([order, open, later], ignoreOrder: true);
        plan.RowsToRemove.ShouldBeEmpty();
    }

    [Test]
    public void ARowStartingOnTheDay_StaysAndIsTrimmed()
    {
        var order = Row(ShiftStatus.SealedOrder, new DateOnly(2026, 1, 1));
        var piece = Row(ShiftStatus.SplitShift, Day);

        var plan = ShiftFamilyEndPlanner.Build([order, piece], OrderId, Day);

        plan.RowsToRemove.ShouldBeEmpty();
        plan.RowsToTrim.ShouldContain(piece);
    }

    [Test]
    public void ARowStartingAfterTheDay_GoesWithItsWholeSubtree_AndIsNotTrimmed()
    {
        var order = Row(ShiftStatus.SealedOrder, new DateOnly(2026, 1, 1));
        var parent = Row(ShiftStatus.SplitShift, Day.AddDays(1));
        var child = Row(ShiftStatus.SplitShift, Day.AddDays(2), parentId: parent.Id);
        var grandchild = Row(ShiftStatus.SplitShift, Day.AddDays(3), parentId: child.Id);

        var plan = ShiftFamilyEndPlanner.Build([order, grandchild, child, parent], OrderId, Day);

        plan.RowsToRemove.ShouldBe([parent, child, grandchild], ignoreOrder: true);
        plan.RowsToTrim.ShouldBe([order]);
    }

    [Test]
    public void AChildThatStartsEarlierThanItsRemovedParent_GoesWithTheParent()
    {
        var order = Row(ShiftStatus.SealedOrder, new DateOnly(2026, 1, 1));
        var parent = Row(ShiftStatus.SplitShift, Day.AddDays(1));
        var child = Row(ShiftStatus.SplitShift, new DateOnly(2026, 1, 1), parentId: parent.Id);

        var plan = ShiftFamilyEndPlanner.Build([order, parent, child], OrderId, Day);

        plan.RowsToRemove.ShouldBe([parent, child], ignoreOrder: true);
    }

    [Test]
    public void ALaterPlannableCopyAfterAReset_IsRemovedToo()
    {
        var order = Row(ShiftStatus.SealedOrder, new DateOnly(2026, 1, 1));
        var closed = Row(ShiftStatus.SplitShift, new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30));
        var fresh = Row(ShiftStatus.OriginalShift, new DateOnly(2026, 7, 1));

        var plan = ShiftFamilyEndPlanner.Build([order, closed, fresh], OrderId, Day);

        plan.RowsToRemove.ShouldBe([fresh]);
        plan.RowsToTrim.ShouldBe([order, closed], ignoreOrder: true);
    }

    [Test]
    public void TheSealedOrder_IsNeverRemoved_EvenWhenItStartsAfterTheDay()
    {
        var order = Row(ShiftStatus.SealedOrder, Day.AddDays(1));

        var plan = ShiftFamilyEndPlanner.Build([order], OrderId, Day);

        plan.RowsToRemove.ShouldBeEmpty();
    }
}
