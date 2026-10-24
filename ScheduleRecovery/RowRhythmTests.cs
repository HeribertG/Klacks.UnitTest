// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleRecovery.Engine;
using Klacks.ScheduleRecovery.Model;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// The shared row-rhythm measure the automatic replacement and the planner's manual candidate search both use.
/// </summary>
[TestFixture]
public sealed class RowRhythmTests
{
    private static readonly DateOnly Day = new(2026, 6, 3);

    private static Func<DateOnly, ShiftCategory> Row(params (int Offset, ShiftCategory Kind)[] works)
    {
        var byDate = works.ToDictionary(w => Day.AddDays(w.Offset), w => w.Kind);
        return date => byDate.TryGetValue(date, out var kind) ? kind : ShiftCategory.Free;
    }

    [Test]
    public void LonelyShiftInAFreeRow_CostsTheIsolatedPenalty()
    {
        RowRhythm.CoverDelta(Row(), Day, ShiftCategory.Early).ShouldBe(RowRhythm.IsolatedWorkDay);
    }

    [Test]
    public void ClosingAGapInABlockOfTheSameKind_Improves()
    {
        var row = Row((-1, ShiftCategory.Early), (1, ShiftCategory.Early));

        RowRhythm.CoverDelta(row, Day, ShiftCategory.Early).ShouldBeLessThan(0);
    }

    [Test]
    public void ExtendingABlockWithADifferentKind_IsWorseThanWithTheSameKind()
    {
        var row = Row((-1, ShiftCategory.Late));

        RowRhythm.CoverDelta(row, Day, ShiftCategory.Early)
            .ShouldBeGreaterThan(RowRhythm.CoverDelta(row, Day, ShiftCategory.Late));
    }

    [Test]
    public void BackwardRotation_CostsMoreThanForwardRotation()
    {
        var row = Row((-1, ShiftCategory.Late));

        RowRhythm.CoverDelta(row, Day, ShiftCategory.Early)
            .ShouldBeGreaterThan(RowRhythm.CoverDelta(row, Day, ShiftCategory.Night));
    }

    [Test]
    public void DayThatAlreadyCarriesWork_KeepsItsRhythm()
    {
        var row = Row((0, ShiftCategory.Early));

        RowRhythm.CoverDelta(row, Day, ShiftCategory.Late).ShouldBe(0);
    }
}
