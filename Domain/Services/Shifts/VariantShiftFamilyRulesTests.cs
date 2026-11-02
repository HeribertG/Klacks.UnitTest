// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the rules a family must meet before it is copied into a variant (key): every live group of every row lies in the
/// allowed hierarchy and there is at least one; the period test is an intersection that includes both boundary days and
/// treats a missing end date as open; the sealed order is found among the rows; a sealed order is the root of its family,
/// every other row points at it through OriginalId, and a row without one has no family and stands alone.
/// </summary>

using Klacks.Api.Domain.Services.Shifts;

namespace Klacks.UnitTest.Domain.Services.Shifts;

[TestFixture]
public class VariantShiftFamilyRulesTests
{
    private static readonly DateOnly PeriodFrom = new(2099, 8, 1);
    private static readonly DateOnly PeriodUntil = new(2099, 8, 31);

    private Guid _allowedGroup;
    private Guid _foreignGroup;
    private IReadOnlySet<Guid> _allowed = null!;

    [SetUp]
    public void SetUp()
    {
        _allowedGroup = Guid.NewGuid();
        _foreignGroup = Guid.NewGuid();
        _allowed = new HashSet<Guid> { _allowedGroup };
    }

    [Test]
    public void HasOnlyAllowedGroups_IsTrueWhenEveryLiveGroupOfEveryRowIsAllowed()
    {
        var family = new[] { Row(_allowedGroup), Row(_allowedGroup) };

        VariantShiftFamilyRules.HasOnlyAllowedGroups(family, _allowed).ShouldBeTrue();
    }

    [Test]
    public void HasOnlyAllowedGroups_IsFalseWhenOneRowCarriesAForeignGroup()
    {
        var family = new[] { Row(_allowedGroup), Row(_allowedGroup, _foreignGroup) };

        VariantShiftFamilyRules.HasOnlyAllowedGroups(family, _allowed).ShouldBeFalse();
    }

    [Test]
    public void HasOnlyAllowedGroups_IsFalseWithoutAnyGroup()
    {
        VariantShiftFamilyRules.HasOnlyAllowedGroups([Row(), Row()], _allowed).ShouldBeFalse();
    }

    [Test]
    public void HasOnlyAllowedGroups_IgnoresADeletedForeignGroupItem()
    {
        var row = Row(_allowedGroup);
        row.GroupItems.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = _foreignGroup, ShiftId = row.Id, IsDeleted = true });

        VariantShiftFamilyRules.HasOnlyAllowedGroups([row], _allowed).ShouldBeTrue();
    }

    [Test]
    public void HasOnlyAllowedGroups_IsFalseWhenOnlyADeletedGroupItemExists()
    {
        var row = Row();
        row.GroupItems.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = _allowedGroup, ShiftId = row.Id, IsDeleted = true });

        VariantShiftFamilyRules.HasOnlyAllowedGroups([row], _allowed).ShouldBeFalse();
    }

    [TestCase(-10, -1, false)]
    [TestCase(-10, 0, true)]
    [TestCase(0, 30, true)]
    [TestCase(30, 40, true)]
    [TestCase(31, 40, false)]
    [TestCase(-10, null, true)]
    [TestCase(31, null, false)]
    public void IntersectsPeriod_IncludesBothBoundaryDaysAndTreatsNoEndAsOpen(int fromOffset, int? untilOffset, bool expected)
    {
        var order = new Shift
        {
            Status = ShiftStatus.SealedOrder,
            FromDate = PeriodFrom.AddDays(fromOffset),
            UntilDate = untilOffset is { } offset ? PeriodFrom.AddDays(offset) : null
        };

        VariantShiftFamilyRules.IntersectsPeriod(order, PeriodFrom, PeriodUntil).ShouldBe(expected);
    }

    [Test]
    public void FindSealedOrder_ReturnsTheSealedRowAndNullWithoutOne()
    {
        var sealedRow = new Shift { Id = Guid.NewGuid(), Status = ShiftStatus.SealedOrder };
        var plannable = new Shift { Id = Guid.NewGuid(), Status = ShiftStatus.OriginalShift };

        VariantShiftFamilyRules.FindSealedOrder([plannable, sealedRow]).ShouldBe(sealedRow);
        VariantShiftFamilyRules.FindSealedOrder([plannable]).ShouldBeNull();
    }

    [Test]
    public void FamilyRootOf_SealedOrder_IsTheOrderItself_WhateverItsOriginalId()
    {
        var order = new Shift { Id = Guid.NewGuid(), Status = ShiftStatus.SealedOrder, OriginalId = Guid.NewGuid() };

        VariantShiftFamilyRules.FamilyRootOf(order).ShouldBe(order.Id);
    }

    [TestCase(ShiftStatus.OriginalShift)]
    [TestCase(ShiftStatus.SplitShift)]
    public void FamilyRootOf_OtherRow_IsItsOriginalId(ShiftStatus status)
    {
        var row = new Shift { Id = Guid.NewGuid(), Status = status, OriginalId = Guid.NewGuid() };

        VariantShiftFamilyRules.FamilyRootOf(row).ShouldBe(row.OriginalId);
    }

    [Test]
    public void FamilyRootOf_RowWithoutOriginalId_HasNoRoot_AndStandsAloneInFamilyRootOrSelf()
    {
        var row = new Shift { Id = Guid.NewGuid(), Status = ShiftStatus.OriginalShift };

        VariantShiftFamilyRules.FamilyRootOf(row).ShouldBeNull();
        VariantShiftFamilyRules.FamilyRootOrSelf(row).ShouldBe(row.Id);
    }

    [Test]
    public void FamilyRootOf_ValuesAndShift_GiveTheSameRoot()
    {
        var originalId = Guid.NewGuid();
        var row = new Shift { Id = Guid.NewGuid(), Status = ShiftStatus.SplitShift, OriginalId = originalId };

        VariantShiftFamilyRules.FamilyRootOf(row.Id, row.Status, row.OriginalId).ShouldBe(VariantShiftFamilyRules.FamilyRootOf(row));
    }

    [TestCase(0, 30, true)]
    [TestCase(5, 10, true)]
    [TestCase(-1, 30, false)]
    [TestCase(0, 31, false)]
    [TestCase(0, null, false)]
    [TestCase(31, 30, false)]
    public void LiesInsidePeriod_IncludesBothBoundaryDaysAndRefusesAnEmptyEnd(int fromOffset, int? untilOffset, bool expected)
    {
        var from = PeriodFrom.AddDays(fromOffset);
        var until = untilOffset is { } offset ? PeriodFrom.AddDays(offset) : (DateOnly?)null;

        VariantShiftFamilyRules.LiesInsidePeriod(from, until, PeriodFrom, PeriodUntil).ShouldBe(expected);
    }

    [Test]
    public void HasCustomer_NeedsACustomerOnEveryRow()
    {
        var customer = Guid.NewGuid();
        var withCustomer = new Shift { Id = Guid.NewGuid(), ClientId = customer };
        var withoutCustomer = new Shift { Id = Guid.NewGuid() };

        VariantShiftFamilyRules.HasCustomer([withCustomer, withCustomer]).ShouldBeTrue();
        VariantShiftFamilyRules.HasCustomer([withCustomer, withoutCustomer]).ShouldBeFalse();
    }

    [Test]
    public void RowsInPeriod_KeepsEveryRowThatOverlapsThePeriod()
    {
        var order = Dated(ShiftStatus.SealedOrder, -30, null);
        var plannable = Dated(ShiftStatus.OriginalShift, -30, null, originalId: order.Id);

        VariantShiftFamilyRules.RowsInPeriod([order, plannable], PeriodFrom, PeriodUntil)
            .ShouldBe([order, plannable], ignoreOrder: true);
    }

    [Test]
    public void RowsInPeriod_LeavesOutPiecesAResetClosedBeforeThePeriod()
    {
        var order = Dated(ShiftStatus.SealedOrder, -60, null);
        var closed = Dated(ShiftStatus.SplitShift, -60, -1, originalId: order.Id);
        closed.RootId = closed.Id;
        var plannable = Dated(ShiftStatus.OriginalShift, 0, null, originalId: order.Id);

        VariantShiftFamilyRules.RowsInPeriod([order, closed, plannable], PeriodFrom, PeriodUntil)
            .ShouldBe([order, plannable], ignoreOrder: true);
    }

    [Test]
    public void RowsInPeriod_LeavesOutAPieceAndItsChildWhenBothLieOutside()
    {
        var order = Dated(ShiftStatus.SealedOrder, 0, 60);
        var inside = Dated(ShiftStatus.SplitShift, 0, 30, originalId: order.Id);
        inside.RootId = inside.Id;
        var outside = Dated(ShiftStatus.SplitShift, 31, 45, originalId: order.Id);
        outside.ParentId = inside.Id;
        outside.RootId = inside.Id;
        var outsideChild = Dated(ShiftStatus.SplitShift, 46, 60, originalId: order.Id);
        outsideChild.ParentId = outside.Id;
        outsideChild.RootId = inside.Id;

        VariantShiftFamilyRules.RowsInPeriod([order, inside, outside, outsideChild], PeriodFrom, PeriodUntil)
            .ShouldBe([order, inside], ignoreOrder: true);
    }

    [Test]
    public void RowsInPeriod_IsNullWhenAPieceInsideHangsOnAParentOutside_BecauseADateCutMakesTheChildDisjoint()
    {
        var order = Dated(ShiftStatus.SealedOrder, -30, 30);
        var parent = Dated(ShiftStatus.SplitShift, -30, -1, originalId: order.Id);
        parent.RootId = parent.Id;
        var child = Dated(ShiftStatus.SplitShift, 0, 30, originalId: order.Id);
        child.ParentId = parent.Id;
        child.RootId = parent.Id;

        VariantShiftFamilyRules.RowsInPeriod([order, parent, child], PeriodFrom, PeriodUntil).ShouldBeNull();
    }

    [Test]
    public void RowsInPeriod_IsNullWhenAPieceInsideOnlyItsRootLiesOutside()
    {
        var order = Dated(ShiftStatus.SealedOrder, -30, 30);
        var root = Dated(ShiftStatus.SplitShift, -30, -1, originalId: order.Id);
        root.RootId = root.Id;
        var piece = Dated(ShiftStatus.SplitShift, 0, 30, originalId: order.Id);
        piece.RootId = root.Id;

        VariantShiftFamilyRules.RowsInPeriod([order, root, piece], PeriodFrom, PeriodUntil).ShouldBeNull();
    }

    [Test]
    public void RowsInPeriod_IsNullWhenOnlyTheSealedOrderOverlaps_BecauseNothingWouldBePlannable()
    {
        var order = Dated(ShiftStatus.SealedOrder, -30, 30);
        var closed = Dated(ShiftStatus.SplitShift, -30, -1, originalId: order.Id);
        closed.RootId = closed.Id;

        VariantShiftFamilyRules.RowsInPeriod([order, closed], PeriodFrom, PeriodUntil).ShouldBeNull();
    }

    [Test]
    public void RowsInPeriod_IsNullWhenTheSealedOrderLiesOutside()
    {
        var order = Dated(ShiftStatus.SealedOrder, -30, -1);
        var plannable = Dated(ShiftStatus.OriginalShift, 0, 30, originalId: order.Id);

        VariantShiftFamilyRules.RowsInPeriod([order, plannable], PeriodFrom, PeriodUntil).ShouldBeNull();
    }

    private static Shift Dated(ShiftStatus status, int fromOffset, int? untilOffset, Guid? originalId = null) => new()
    {
        Id = Guid.NewGuid(),
        Status = status,
        OriginalId = originalId,
        FromDate = PeriodFrom.AddDays(fromOffset),
        UntilDate = untilOffset is { } offset ? PeriodFrom.AddDays(offset) : null
    };

    private static Shift Row(params Guid[] groupIds)
    {
        var shift = new Shift { Id = Guid.NewGuid(), Status = ShiftStatus.OriginalShift };
        foreach (var groupId in groupIds)
        {
            shift.GroupItems.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = shift.Id });
        }

        return shift;
    }
}
