using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Validation;

/// <summary>
/// The stage-3 target-hours guard lets a batch through only when the touched rows do not move away from their
/// target hours (sum, squared sum and largest row deviation); rows without a target are ignored.
/// </summary>
[TestFixture]
public class TargetHoursDeviationGuardTests
{
    private const decimal ShiftHours = 8m;
    private static readonly DateOnly FirstDay = new(2026, 3, 2);

    [Test]
    public void SwapTowardsTheTargets_IsAllowed()
    {
        // Row 0 is 8 h over its target, row 1 8 h under: moving one shift from row 0 to row 1 fixes both.
        var bitmap = Build(targets: [8m, 16m], rowWork: [[true, true], [true, false]]);
        var guard = new TargetHoursDeviationGuard();
        var swap = new PlanCellSwap(0, 1, 1, 1, string.Empty);

        var before = guard.Capture(bitmap, [swap]);
        PlanMutationValidator.Apply(bitmap, swap);

        guard.Diagnose(bitmap, before).ShouldBeNull();
    }

    [Test]
    public void SwapAwayFromTheTargets_IsReported()
    {
        // Both rows sit on target; moving a shift makes one row 8 h over and the other 8 h under.
        var bitmap = Build(targets: [8m, 8m], rowWork: [[true, false], [false, true]]);
        var guard = new TargetHoursDeviationGuard();
        var swap = new PlanCellSwap(0, 0, 1, 0, string.Empty);

        var before = guard.Capture(bitmap, [swap]);
        PlanMutationValidator.Apply(bitmap, swap);

        guard.Diagnose(bitmap, before).ShouldNotBeNull();
    }

    [Test]
    public void RowsWithoutTarget_AreIgnored()
    {
        var bitmap = Build(targets: [0m, 8m], rowWork: [[true, false], [false, true]]);
        var guard = new TargetHoursDeviationGuard();
        var swap = new PlanCellSwap(0, 0, 1, 0, string.Empty);

        var before = guard.Capture(bitmap, [swap]);

        before.Rows.ShouldBe([1]);
    }

    private static HarmonyBitmap Build(decimal[] targets, bool[][] rowWork)
    {
        var dayCount = rowWork[0].Length;
        var rows = targets
            .Select((target, i) => new BitmapAgent($"a{i}", $"Agent {i}", target, new HashSet<CellSymbol>()))
            .ToList();
        var days = Enumerable.Range(0, dayCount).Select(d => FirstDay.AddDays(d)).ToList();
        var cells = new Cell[rows.Count, dayCount];
        for (var r = 0; r < rows.Count; r++)
        {
            for (var d = 0; d < dayCount; d++)
            {
                cells[r, d] = rowWork[r][d]
                    ? new Cell(CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], false, Hours: ShiftHours)
                    : Cell.Free();
            }
        }
        return new HarmonyBitmap(rows, days, cells);
    }
}
