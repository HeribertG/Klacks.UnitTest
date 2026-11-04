// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ShiftResetRules: the start date, what counts as nothing to reset, and which rows stay by date: every row that
/// begins before the start stays (with or without work) together with its ancestors, every row from the start on goes.
/// </summary>

using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Shifts;

namespace Klacks.UnitTest.Services.Shifts;

[TestFixture]
public class ShiftResetRulesTests
{
    private static readonly DateOnly OrderFrom = new(2026, 1, 1);
    private static readonly DateOnly Start = new(2026, 4, 1);

    private static Shift Row(ShiftStatus status, DateOnly from, DateOnly? until = null, Guid? parentId = null) => new()
    {
        Id = Guid.NewGuid(),
        Status = status,
        FromDate = from,
        UntilDate = until,
        ParentId = parentId
    };

    [Test]
    public void EffectiveStart_IsTheRequestedDay_WithOrWithoutWork() =>
        ShiftResetRules.EffectiveStart(OrderFrom, Start).ShouldBe(Start);

    [Test]
    public void EffectiveStart_OnTheFirstDay_IsTheFirstDay() =>
        ShiftResetRules.EffectiveStart(OrderFrom, OrderFrom).ShouldBe(OrderFrom);

    [Test]
    public void EffectiveStart_WithAskedDayBeforeTheOriginRow_IsItsFirstDay() =>
        ShiftResetRules.EffectiveStart(OrderFrom, OrderFrom.AddDays(-3)).ShouldBe(OrderFrom);

    [Test]
    public void EarliestAllowed_WithoutWork_IsTheOrderStart() =>
        ShiftResetRules.EarliestAllowed(OrderFrom, null).ShouldBe(OrderFrom);

    [Test]
    public void EarliestAllowed_WithWork_IsTheDayAfterTheLastWork() =>
        ShiftResetRules.EarliestAllowed(OrderFrom, new DateOnly(2026, 2, 28)).ShouldBe(new DateOnly(2026, 3, 1));

    [Test]
    public void EarliestAllowed_NeverLiesBeforeTheOrder() =>
        ShiftResetRules.EarliestAllowed(OrderFrom, OrderFrom.AddDays(-10)).ShouldBe(OrderFrom);

    [Test]
    public void HasNothingToReset_AnOpenPlannableShiftWithoutOpenPiece_IsTrue()
    {
        var rows = new[] { Row(ShiftStatus.SplitShift, OrderFrom, Start.AddDays(-1)), Row(ShiftStatus.OriginalShift, Start) };

        ShiftResetRules.HasNothingToReset(rows, Start).ShouldBeTrue();
    }

    [Test]
    public void HasNothingToReset_APieceReachingTheStart_IsFalse()
    {
        var rows = new[] { Row(ShiftStatus.SplitShift, OrderFrom, Start), Row(ShiftStatus.OriginalShift, Start) };

        ShiftResetRules.HasNothingToReset(rows, Start).ShouldBeFalse();
    }

    [Test]
    public void HasNothingToReset_NoPlannableShift_IsFalse()
    {
        var rows = new[] { Row(ShiftStatus.SplitShift, OrderFrom, Start.AddDays(-1)) };

        ShiftResetRules.HasNothingToReset(rows, Start).ShouldBeFalse();
    }

    [Test]
    public void HasNothingToReset_AnEarlierDayThanAFormerReset_WithPiecesUpToIt_IsFalse()
    {
        var formerStart = Start.AddDays(30);
        var rows = new[] { Row(ShiftStatus.SplitShift, OrderFrom, formerStart.AddDays(-1)), Row(ShiftStatus.OriginalShift, formerStart) };

        ShiftResetRules.HasNothingToReset(rows, Start).ShouldBeFalse();
    }

    [Test]
    public void Plan_PiecesWithoutWorkBeforeTheStart_Stay_AndEndTheDayBefore_ThePieceFromTheStartGoes()
    {
        var plannable = Row(ShiftStatus.OriginalShift, OrderFrom, OrderFrom.AddDays(9));
        var early = Row(ShiftStatus.SplitShift, OrderFrom.AddDays(10));
        var fromStart = Row(ShiftStatus.SplitShift, Start);

        var plan = ShiftResetRules.Plan([plannable, early, fromStart], Start);

        plan.RowsToClose.ShouldBe([early]);
        plan.RowsToRemove.ShouldBe([fromStart]);
        plan.HasKeptRowFromStart.ShouldBeFalse();
    }

    [Test]
    public void Plan_OnTheFirstDay_RemovesEveryRow_OpenOrClosed()
    {
        var closed = Row(ShiftStatus.SplitShift, OrderFrom, Start.AddDays(-20));
        var open = Row(ShiftStatus.SplitShift, Start);
        var plannable = Row(ShiftStatus.OriginalShift, Start);

        var plan = ShiftResetRules.Plan([closed, open, plannable], OrderFrom);

        plan.RowsToRemove.ShouldBe([closed, open, plannable], ignoreOrder: true);
        plan.RowsToClose.ShouldBeEmpty();
        plan.HasKeptRowFromStart.ShouldBeFalse();
    }

    [Test]
    public void Plan_APieceReachingOverTheStart_IsClosed()
    {
        var reaching = Row(ShiftStatus.SplitShift, OrderFrom, Start.AddDays(40));

        var plan = ShiftResetRules.Plan([reaching], Start);

        plan.RowsToClose.ShouldBe([reaching]);
        plan.RowsToRemove.ShouldBeEmpty();
    }

    [Test]
    public void Plan_APieceEndingBeforeTheStart_StaysAsItIs()
    {
        var ended = Row(ShiftStatus.SplitShift, OrderFrom, Start.AddDays(-10));

        var plan = ShiftResetRules.Plan([ended], Start);

        plan.RowsToClose.ShouldBeEmpty();
        plan.RowsToRemove.ShouldBeEmpty();
    }

    [Test]
    public void Plan_AChildBeforeTheStart_StaysWithItsParent_AndAChildFromTheStartGoes()
    {
        var parent = Row(ShiftStatus.SplitShift, OrderFrom);
        var child = Row(ShiftStatus.SplitShift, OrderFrom.AddDays(5), parentId: parent.Id);
        var laterChild = Row(ShiftStatus.SplitShift, Start.AddDays(3), parentId: parent.Id);

        var plan = ShiftResetRules.Plan([parent, child, laterChild], Start);

        plan.RowsToRemove.ShouldBe([laterChild]);
        plan.RowsToClose.ShouldBe([parent, child], ignoreOrder: true);
        plan.HasKeptRowFromStart.ShouldBeFalse();
    }

    [Test]
    public void Plan_AParentFromTheStartAboveAChildBeforeIt_IsKept_AndFlagged()
    {
        var parent = Row(ShiftStatus.SplitShift, Start);
        var child = Row(ShiftStatus.SplitShift, OrderFrom, parentId: parent.Id);

        var plan = ShiftResetRules.Plan([parent, child], Start);

        plan.RowsToRemove.ShouldBeEmpty();
        plan.HasKeptRowFromStart.ShouldBeTrue();
    }
}
