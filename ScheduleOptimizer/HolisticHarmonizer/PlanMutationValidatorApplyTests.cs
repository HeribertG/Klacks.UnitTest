// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer;

/// <summary>
/// <see cref="PlanMutationValidator.Apply"/> re-anchors the two cells of a cross-day swap on their new day (same time of
/// day), and applying the swap a second time - the rollback every caller uses (BatchEvaluator, the deterministic search)
/// - restores the original times exactly. A same-day swap keeps the cell instances, so reference-based caches stay valid.
/// </summary>
[TestFixture]
public sealed class PlanMutationValidatorApplyTests
{
    private static readonly DateOnly Monday = new(2026, 1, 5);

    [Test]
    public void CrossDaySwap_ReanchorsBothCellsOnTheirTargetDay()
    {
        var bitmap = Bitmap();

        PlanMutationValidator.Apply(bitmap, CrossDay());

        bitmap.GetCell(0, 0).StartAt.ShouldBe(Monday.ToDateTime(new TimeOnly(22, 0)));
        bitmap.GetCell(0, 0).EndAt.ShouldBe(Monday.AddDays(1).ToDateTime(new TimeOnly(6, 0)));
        bitmap.GetCell(1, 3).StartAt.ShouldBe(Monday.AddDays(3).ToDateTime(new TimeOnly(6, 0)));
        bitmap.GetCell(1, 3).EndAt.ShouldBe(Monday.AddDays(3).ToDateTime(new TimeOnly(14, 0)));
    }

    [Test]
    public void ApplyingACrossDaySwapTwice_RestoresStartAndEndExactly()
    {
        var bitmap = Bitmap();
        var originalA = bitmap.GetCell(0, 0);
        var originalB = bitmap.GetCell(1, 3);

        PlanMutationValidator.Apply(bitmap, CrossDay());
        PlanMutationValidator.Apply(bitmap, CrossDay());

        bitmap.GetCell(0, 0).ShouldBe(originalA);
        bitmap.GetCell(1, 3).ShouldBe(originalB);
        bitmap.GetCell(0, 0).StartAt.Kind.ShouldBe(originalA.StartAt.Kind);
    }

    [Test]
    public void CrossDaySwapOfCellsWithoutTimes_KeepsTheTimesUnset()
    {
        var days = new List<DateOnly> { Monday, Monday.AddDays(1) };
        var cells = new Cell[2, 2];
        cells[0, 0] = new Cell(CellSymbol.Early, Guid.NewGuid(), [], false, default, default, 8m);
        cells[0, 1] = Cell.Free();
        cells[1, 0] = Cell.Free();
        cells[1, 1] = new Cell(CellSymbol.Late, Guid.NewGuid(), [], false, default, default, 8m);
        var bitmap = new HarmonyBitmap([Agent("A"), Agent("B")], days, cells);

        PlanMutationValidator.Apply(bitmap, new PlanCellSwap(0, 0, 1, 1, "test"));

        bitmap.GetCell(0, 0).StartAt.ShouldBe(default);
        bitmap.GetCell(0, 0).EndAt.ShouldBe(default);
        bitmap.GetCell(1, 1).StartAt.ShouldBe(default);
    }

    [Test]
    public void SameDaySwap_KeepsTheCellInstances()
    {
        var bitmap = Bitmap();
        var cellA = bitmap.GetCell(0, 3);
        var cellB = bitmap.GetCell(1, 3);

        PlanMutationValidator.Apply(bitmap, new PlanCellSwap(0, 3, 1, 3, "test"));

        ReferenceEquals(bitmap.GetCell(0, 3), cellB).ShouldBeTrue();
        ReferenceEquals(bitmap.GetCell(1, 3), cellA).ShouldBeTrue();
    }

    private static PlanCellSwap CrossDay() => new(RowA: 0, DayA: 0, RowB: 1, DayB: 3, Reason: "test");

    /// <summary>Row 0 works early on Monday (day 0), row 1 a night on Thursday (day 3); the other cells are free.</summary>
    private static HarmonyBitmap Bitmap()
    {
        var days = Enumerable.Range(0, 4).Select(offset => Monday.AddDays(offset)).ToList();
        var cells = new Cell[2, 4];
        for (var day = 0; day < days.Count; day++)
        {
            cells[0, day] = Cell.Free();
            cells[1, day] = Cell.Free();
        }

        cells[0, 0] = new Cell(
            CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], false,
            Monday.ToDateTime(new TimeOnly(6, 0)), Monday.ToDateTime(new TimeOnly(14, 0)), 8m);
        cells[1, 3] = new Cell(
            CellSymbol.Night, Guid.NewGuid(), [Guid.NewGuid()], false,
            Monday.AddDays(3).ToDateTime(new TimeOnly(22, 0)), Monday.AddDays(4).ToDateTime(new TimeOnly(6, 0)), 8m);
        return new HarmonyBitmap([Agent("A"), Agent("B")], days, cells);
    }

    private static BitmapAgent Agent(string id) => new(id, id, 0m, new HashSet<CellSymbol>());
}
