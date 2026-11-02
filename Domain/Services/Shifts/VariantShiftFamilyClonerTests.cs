// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins how an order family is copied into a variant (key): new unique ids from one map, the variant token on the shifts
/// and their group items, no scenario source, a zero child-count snapshot, no nested-set values, OriginalId, ParentId and
/// RootId pointing into the copy (a target outside the copied rows becomes null, never the old id), expenses and required
/// qualifications copied by the new shift id with new row ids and no token, soft-deleted requirements left out, and the
/// source rows untouched.
/// </summary>

using Klacks.Api.Domain.Services.Shifts;

namespace Klacks.UnitTest.Domain.Services.Shifts;

[TestFixture]
public class VariantShiftFamilyClonerTests
{
    private const int SourceLft = 1;
    private const int SourceRgt = 4;
    private const int SourceChildCount = 7;
    private const decimal ExpenseAmount = 3.5m;

    private static readonly DateOnly PeriodFrom = new(2099, 12, 1);
    private static readonly DateOnly PeriodUntil = new(2099, 12, 14);

    private Guid _token;
    private Guid _groupId;
    private Guid _qualificationId;
    private Shift _order = null!;
    private Shift _plannable = null!;
    private Shift _piece = null!;
    private Guid _outsideId;

    [SetUp]
    public void SetUp()
    {
        _token = Guid.NewGuid();
        _groupId = Guid.NewGuid();
        _qualificationId = Guid.NewGuid();
        _outsideId = Guid.NewGuid();
        _order = NewShift(ShiftStatus.SealedOrder);
        _plannable = NewShift(ShiftStatus.OriginalShift);
        _plannable.OriginalId = _order.Id;
        _plannable.Lft = SourceLft;
        _plannable.Rgt = SourceRgt;
        _piece = NewShift(ShiftStatus.SplitShift);
        _piece.OriginalId = _order.Id;
        _piece.ParentId = _plannable.Id;
        _piece.RootId = _plannable.Id;
        _piece.Lft = SourceLft + 1;
        _piece.Rgt = SourceRgt - 1;
        _piece.ScenarioSourceShiftId = _outsideId;
        _piece.SourceChildCountSnapshot = SourceChildCount;
    }

    [Test]
    public void Clone_GivesNewUniqueIdsTheTokenAndNoSourceOrTreeValues()
    {
        var clones = VariantShiftFamilyCloner.Clone([_order, _plannable, _piece], _token, PeriodFrom, PeriodUntil);

        clones.Count.ShouldBe(3);
        clones.Select(clone => clone.Id).Distinct().Count().ShouldBe(3);
        clones.Select(clone => clone.Id).ShouldNotContain(_order.Id);
        clones.Select(clone => clone.Id).ShouldNotContain(_plannable.Id);
        clones.Select(clone => clone.Id).ShouldNotContain(_piece.Id);
        clones.ShouldAllBe(clone => clone.AnalyseToken == _token
            && clone.ScenarioSourceShiftId == null
            && clone.SourceChildCountSnapshot == 0
            && clone.Lft == null
            && clone.Rgt == null);
        clones.Select(clone => clone.Status).ShouldBe(
            new[] { ShiftStatus.SealedOrder, ShiftStatus.OriginalShift, ShiftStatus.SplitShift });
    }

    [Test]
    public void Clone_LinksOriginalParentAndRootIntoTheCopy()
    {
        var clones = VariantShiftFamilyCloner.Clone([_order, _plannable, _piece], _token, PeriodFrom, PeriodUntil);
        var order = clones.Single(clone => clone.Status == ShiftStatus.SealedOrder);
        var plannable = clones.Single(clone => clone.Status == ShiftStatus.OriginalShift);
        var piece = clones.Single(clone => clone.Status == ShiftStatus.SplitShift);

        order.OriginalId.ShouldBeNull();
        plannable.OriginalId.ShouldBe(order.Id);
        piece.OriginalId.ShouldBe(order.Id);
        piece.ParentId.ShouldBe(plannable.Id);
        piece.RootId.ShouldBe(plannable.Id);
    }

    [Test]
    public void Clone_ALinkTargetOutsideTheCopiedRows_BecomesNullAndNeverKeepsTheRealId()
    {
        _piece.ParentId = _outsideId;
        _piece.RootId = _outsideId;

        var clones = VariantShiftFamilyCloner.Clone([_order, _plannable, _piece], _token, PeriodFrom, PeriodUntil);
        var piece = clones.Single(clone => clone.Status == ShiftStatus.SplitShift);

        piece.ParentId.ShouldBeNull();
        piece.RootId.ShouldBeNull();
        clones.SelectMany(clone => new[] { clone.OriginalId, clone.ParentId, clone.RootId })
            .ShouldNotContain(_outsideId);
    }

    [Test]
    public void Clone_CopiesGroupItemsWithNewIdsTheCloneShiftIdAndTheToken()
    {
        var clones = VariantShiftFamilyCloner.Clone([_order, _plannable], _token, PeriodFrom, PeriodUntil);

        foreach (var clone in clones)
        {
            var item = clone.GroupItems.ShouldHaveSingleItem();
            item.GroupId.ShouldBe(_groupId);
            item.ShiftId.ShouldBe(clone.Id);
            item.AnalyseToken.ShouldBe(_token);
            item.Id.ShouldNotBe(Guid.Empty);
        }

        clones.SelectMany(clone => clone.GroupItems).Select(item => item.Id).Distinct().Count().ShouldBe(2);
        clones.SelectMany(clone => clone.GroupItems).Select(item => item.Id)
            .ShouldNotContain(_order.GroupItems[0].Id);
    }

    [Test]
    public void Clone_CopiesExpensesByTheNewShiftIdWithoutAToken()
    {
        var clones = VariantShiftFamilyCloner.Clone([_order, _plannable], _token, PeriodFrom, PeriodUntil);

        foreach (var clone in clones)
        {
            var expense = clone.ShiftExpenses.ShouldHaveSingleItem();
            expense.ShiftId.ShouldBe(clone.Id);
            expense.AnalyseToken.ShouldBeNull();
            expense.Amount.ShouldBe(ExpenseAmount);
            expense.Id.ShouldNotBe(Guid.Empty);
        }

        clones.SelectMany(clone => clone.ShiftExpenses).Select(expense => expense.Id).Distinct().Count().ShouldBe(2);
    }

    [Test]
    public void Clone_CopiesLiveRequiredQualificationsByTheNewShiftIdAndLeavesDeletedOnesOut()
    {
        _order.RequiredQualifications.Add(new ShiftRequiredQualification
        {
            Id = Guid.NewGuid(),
            ShiftId = _order.Id,
            QualificationId = Guid.NewGuid(),
            IsDeleted = true
        });

        var clones = VariantShiftFamilyCloner.Clone([_order, _plannable], _token, PeriodFrom, PeriodUntil);

        foreach (var clone in clones)
        {
            var requirement = clone.RequiredQualifications.ShouldHaveSingleItem();
            requirement.ShiftId.ShouldBe(clone.Id);
            requirement.QualificationId.ShouldBe(_qualificationId);
            requirement.IsMandatory.ShouldBeTrue();
            requirement.Id.ShouldNotBe(Guid.Empty);
        }

        clones.SelectMany(clone => clone.RequiredQualifications).Select(requirement => requirement.Id)
            .Distinct().Count().ShouldBe(2);
    }

    [Test]
    public void Clone_LeavesTheSourceRowsUntouched()
    {
        var sourceGroupItem = _order.GroupItems[0];
        var sourceExpense = _order.ShiftExpenses[0];
        var sourceRequirement = _order.RequiredQualifications[0];

        VariantShiftFamilyCloner.Clone([_order, _plannable, _piece], _token, PeriodFrom, PeriodUntil);

        _order.AnalyseToken.ShouldBeNull();
        _plannable.Lft.ShouldBe(SourceLft);
        _plannable.Rgt.ShouldBe(SourceRgt);
        _piece.ParentId.ShouldBe(_plannable.Id);
        _piece.ScenarioSourceShiftId.ShouldBe(_outsideId);
        _piece.SourceChildCountSnapshot.ShouldBe(SourceChildCount);
        sourceGroupItem.AnalyseToken.ShouldBeNull();
        sourceGroupItem.ShiftId.ShouldBe(_order.Id);
        sourceExpense.ShiftId.ShouldBe(_order.Id);
        sourceRequirement.ShiftId.ShouldBe(_order.Id);
    }

    [Test]
    public void Clone_ClipsEveryRowToThePeriod_AndAnOpenEndBecomesThePeriodEnd()
    {
        _order.FromDate = PeriodFrom.AddDays(-100);
        _order.UntilDate = null;
        _plannable.FromDate = PeriodFrom.AddDays(-100);
        _plannable.UntilDate = PeriodUntil.AddDays(50);
        _piece.FromDate = PeriodFrom.AddDays(2);
        _piece.UntilDate = PeriodUntil.AddDays(-2);

        var clones = VariantShiftFamilyCloner.Clone([_order, _plannable, _piece], _token, PeriodFrom, PeriodUntil);

        var order = clones.Single(clone => clone.Status == ShiftStatus.SealedOrder);
        order.FromDate.ShouldBe(PeriodFrom);
        order.UntilDate.ShouldBe(PeriodUntil);
        var plannable = clones.Single(clone => clone.Status == ShiftStatus.OriginalShift);
        plannable.FromDate.ShouldBe(PeriodFrom);
        plannable.UntilDate.ShouldBe(PeriodUntil);
        var piece = clones.Single(clone => clone.Status == ShiftStatus.SplitShift);
        piece.FromDate.ShouldBe(PeriodFrom.AddDays(2));
        piece.UntilDate.ShouldBe(PeriodUntil.AddDays(-2));
    }

    [Test]
    public void Clone_LeavesTheSourceDatesAlone()
    {
        _order.FromDate = PeriodFrom.AddDays(-100);
        _order.UntilDate = null;

        VariantShiftFamilyCloner.Clone([_order], _token, PeriodFrom, PeriodUntil);

        _order.FromDate.ShouldBe(PeriodFrom.AddDays(-100));
        _order.UntilDate.ShouldBeNull();
    }

    [Test]
    public void Clone_NamesTheRealSealedOrderOnTheCopiedSealedOrderOnly()
    {
        var clones = VariantShiftFamilyCloner.Clone([_order, _plannable, _piece], _token, PeriodFrom, PeriodUntil);

        clones.Single(clone => clone.Status == ShiftStatus.SealedOrder).CopiedFromShiftId.ShouldBe(_order.Id);
        clones.Where(clone => clone.Status != ShiftStatus.SealedOrder).ShouldAllBe(clone => clone.CopiedFromShiftId == null);
    }

    private Shift NewShift(ShiftStatus status)
    {
        var shift = new Shift { Id = Guid.NewGuid(), Name = status.ToString(), Status = status, ClientId = Guid.NewGuid() };
        shift.GroupItems.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = _groupId, ShiftId = shift.Id });
        shift.ShiftExpenses.Add(new ShiftExpenses
        {
            Id = Guid.NewGuid(),
            ShiftId = shift.Id,
            Amount = ExpenseAmount,
            Description = "Expense"
        });
        shift.RequiredQualifications.Add(new ShiftRequiredQualification
        {
            Id = Guid.NewGuid(),
            ShiftId = shift.Id,
            QualificationId = _qualificationId,
            IsMandatory = true
        });
        return shift;
    }
}
