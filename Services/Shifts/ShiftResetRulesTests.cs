// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ShiftResetRules: the start date with and without work, what counts as nothing to reset, and which rows stay.
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
    public void EffectiveStart_WithoutWork_IsTheOrderStart_WhateverWasAskedFor() =>
        ShiftResetRules.EffectiveStart(OrderFrom, null, Start).ShouldBe(OrderFrom);

    [Test]
    public void EffectiveStart_WithWork_IsTheRequestedDay() =>
        ShiftResetRules.EffectiveStart(OrderFrom, Start.AddDays(-5), Start).ShouldBe(Start);

    [Test]
    public void EffectiveStart_WithAskedDayBeforeTheOrder_IsTheOrderStart() =>
        ShiftResetRules.EffectiveStart(OrderFrom, OrderFrom.AddDays(-9), OrderFrom.AddDays(-3)).ShouldBe(OrderFrom);

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
    public void Plan_WithoutWork_RemovesEveryRow_OpenOrClosed()
    {
        var closed = Row(ShiftStatus.SplitShift, OrderFrom, Start.AddDays(-20));
        var open = Row(ShiftStatus.SplitShift, Start);
        var plannable = Row(ShiftStatus.OriginalShift, Start);

        var plan = ShiftResetRules.Plan([closed, open, plannable], new HashSet<Guid>(), OrderFrom);

        plan.RowsToRemove.ShouldBe([closed, open, plannable], ignoreOrder: true);
        plan.RowsToClose.ShouldBeEmpty();
        plan.HasKeptRowFromStart.ShouldBeFalse();
    }

    [Test]
    public void Plan_APieceWithWork_StaysAndCloses_AndTheOthersGo()
    {
        var worked = Row(ShiftStatus.SplitShift, OrderFrom);
        var later = Row(ShiftStatus.SplitShift, Start.AddDays(30));

        var plan = ShiftResetRules.Plan([worked, later], new HashSet<Guid> { worked.Id }, Start);

        plan.RowsToClose.ShouldBe([worked]);
        plan.RowsToRemove.ShouldBe([later]);
    }

    [Test]
    public void Plan_AKeptPieceEndingBeforeTheStart_StaysAsItIs()
    {
        var worked = Row(ShiftStatus.SplitShift, OrderFrom, Start.AddDays(-10));

        var plan = ShiftResetRules.Plan([worked], new HashSet<Guid> { worked.Id }, Start);

        plan.RowsToClose.ShouldBeEmpty();
        plan.RowsToRemove.ShouldBeEmpty();
    }

    [Test]
    public void Plan_TheAncestorsOfAPieceWithWork_Stay_AndASiblingWithoutWorkGoes()
    {
        var parent = Row(ShiftStatus.SplitShift, OrderFrom);
        var worked = Row(ShiftStatus.SplitShift, OrderFrom, parentId: parent.Id);
        var sibling = Row(ShiftStatus.SplitShift, OrderFrom, parentId: parent.Id);

        var plan = ShiftResetRules.Plan([parent, worked, sibling], new HashSet<Guid> { worked.Id }, Start);

        plan.RowsToRemove.ShouldBe([sibling]);
        plan.RowsToClose.ShouldBe([parent, worked], ignoreOrder: true);
    }

    [Test]
    public void Plan_AKeptRowStartingOnTheStart_IsFlagged()
    {
        var worked = Row(ShiftStatus.SplitShift, Start);

        ShiftResetRules.Plan([worked], new HashSet<Guid> { worked.Id }, Start).HasKeptRowFromStart.ShouldBeTrue();
    }
}
