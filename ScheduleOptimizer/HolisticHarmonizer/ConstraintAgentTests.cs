// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Committee;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Committee.Agents;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer;

[TestFixture]
public class ConstraintAgentTests
{
    private static readonly DateOnly Day0 = new(2026, 1, 5);
    private static readonly Cell FreeCell = Cell.Free();

    [Test]
    public void Hours_SwapMovesBothRowsTowardTarget_Approves()
    {
        // rowA target=8h, currently 0 (free at day 0). rowB target=8h, currently 16h (work cells day 0+1).
        // Swap day 0: rowA gets work (+8 → 8h, perfect), rowB gets free (-8 → 8h, perfect).
        var bitmap = BuildBitmap(
            (target: 8m, day0: FreeCell, day1: FreeCell),
            (target: 8m, day0: WorkCell(CellSymbol.Early, hours: 8m), day1: WorkCell(CellSymbol.Late, hours: 8m)));

        var verdict = new HoursConstraintAgent().Evaluate(bitmap, new PlanCellSwap(0, 0, 1, 0, "test"));
        verdict.Vote.ShouldBe(ConstraintAgentVote.Approve);
    }

    [Test]
    public void Hours_SwapPushesBothRowsAwayFromTarget_Vetoes()
    {
        // rowA target=8h, currently 8h (perfect). rowB target=8h, currently 8h (perfect).
        // Swap day 0: rowA gets Late instead of Early (still 8h work, no hour change → equal hours → abstain trigger).
        // We need different hour values to trigger non-abstain.
        // rowA target=4h, currently 8h (over). rowB target=12h, currently 8h (under).
        // Swap rowA day0 (8h work) ↔ rowB day0 (free).
        // After: rowA=0h (deviation 4 → 4 same), rowB=16h (deviation 4 → 4 same). Hmm.
        // Use bigger hour numbers:
        // rowA target=16h, currently 8h (deviation -8). rowB target=4h, currently 8h (deviation +4).
        // Swap rowA day0 (free 0h) ↔ rowB day0 (work 8h).
        // After: rowA=16h (dev 0), rowB=0h (dev -4). Both improve → approve.
        // To get veto: both rows worsen.
        // rowA target=4h currently 8h (dev +4). rowB target=12h currently 8h (dev -4).
        // rowA day0 = work 8h, rowB day0 = free 0h.
        // Swap: rowA=0h (dev -4), rowB=16h (dev +4). Both worsen by same magnitude → tie. We want strict worsening.
        // rowA target=8h currently 16h (dev +8). rowB target=8h currently 0h (dev -8).
        // rowA day0 = work 8h, rowB day0 = free 0h.
        // Swap: rowA=8h (dev 0), rowB=8h (dev 0). Both improve → approve, not what we want.
        // Veto needs both rows to be on the same side (e.g. both over-target).
        // rowA target=4h currently 8h (dev +4). rowB target=4h currently 0h (dev -4).
        // rowA day0 = work 8h, rowB day0 = free 0h.
        // Swap: rowA=0h (dev -4), rowB=8h (dev +4). Same magnitude, tie → abstain.
        // Need asymmetric hours. Let cellA=8h, cellB=4h.
        // rowA target=4h currently 12h (one cell 8h + one cell 4h). rowB target=4h currently 0h.
        // Set rowA day0=8h work, rowA day1=4h work. rowB day0=4h work, rowB day1=free.
        // Swap rowA day0 (8h) ↔ rowB day0 (4h). After: rowA = 4h+4h = 8h (dev +4), rowB = 8h+0 = 8h (dev +4).
        // Before: rowA = 12h (dev +8), rowB = 4h (dev 0). After: rowA dev 4 (improves -4), rowB dev 4 (worsens +4).
        // Mixed → abstain. Try another combo.
        // rowA target=4h currently 16h (two 8h cells). rowB target=4h currently 0h.
        // Swap rowA day0 (8h) ↔ rowB day0 (free 0h). After: rowA = 8h (dev +4), rowB = 8h (dev +4).
        // Before: rowA dev +12, rowB dev -4. After: rowA improves (-8), rowB worsens (+8) → mixed → abstain.
        // Veto requires BOTH worsen. rowA gets less work but already under → worse. rowB gets less work but already under → worse. So both must be under-target before AND swap reduces both.
        // But swap exchanges — one always gains, one always loses (in hour units, since cells exchange).
        // Actually both can worsen IFF the gainer was already over-target AND the loser was already under-target.
        // rowA target=4 currently 0 (under). rowB target=4 currently 8 (over).
        // Swap rowA day0 (free 0h) ↔ rowB day0 (work 8h). After: rowA = 8 (over), rowB = 0 (under).
        // Both flipped from under/over to over/under, distance unchanged. Tie → abstain.
        // To strictly worsen both: rowA target=2, currently 0. rowB target=2, currently 4.
        // Cells: rowA day0=free, rowB day0=work 4h. Swap. After: rowA=4h (dev +2 vs before 2), rowB=0h (dev -2 vs before 2). Same dist → abstain.
        // Different hour values let one go further:
        // rowA target=3, currently 0 (dev 3). rowB target=3, currently 6 (dev 3).
        // rowA day0=free, rowB day0=work 6h. Swap. After: rowA=6 (dev +3), rowB=0 (dev -3). Same → abstain.
        //
        // Conclusion: For both to STRICTLY worsen with same swap, requires asymmetric hour cells.
        // rowA target=10, currently 8 (under, dev -2). rowB target=10, currently 12 (over, dev +2).
        // rowA day0 = work 8h, rowA day1 = free 0h. (sum=8, target 10, dev -2)
        // rowB day0 = work 4h, rowB day1 = work 8h. (sum=12, target 10, dev +2)
        // Swap rowA day0 (8h) ↔ rowB day0 (4h). After:
        // rowA day0 = 4h, sum = 4h (dev -6, was -2, worsened by 4).
        // rowB day0 = 8h, sum = 16h (dev +6, was +2, worsened by 4). ← Both worsen!
        var bitmap = BuildBitmap(
            (target: 10m, day0: WorkCell(CellSymbol.Late, hours: 8m), day1: FreeCell),
            (target: 10m, day0: WorkCell(CellSymbol.Early, hours: 4m), day1: WorkCell(CellSymbol.Late, hours: 8m)));

        var verdict = new HoursConstraintAgent().Evaluate(bitmap, new PlanCellSwap(0, 0, 1, 0, "test"));
        verdict.Vote.ShouldBe(ConstraintAgentVote.Veto);
    }

    [Test]
    public void Hours_IdenticalCellHours_Abstains()
    {
        var bitmap = BuildBitmap(
            (target: 8m, day0: WorkCell(CellSymbol.Early, hours: 8m), day1: FreeCell),
            (target: 8m, day0: WorkCell(CellSymbol.Late, hours: 8m), day1: FreeCell));

        var verdict = new HoursConstraintAgent().Evaluate(bitmap, new PlanCellSwap(0, 0, 1, 0, "test"));
        verdict.Vote.ShouldBe(ConstraintAgentVote.Abstain);
    }

    [Test]
    public void Pause_SwapIntroducesNightToEarlyTransition_Vetoes()
    {
        // rowA day0 = Late (will become Night via swap), rowA day1 = Early (problematic neighbour).
        // rowB day0 = Night (will become Late). rowB day1 = free.
        var bitmap = BuildBitmap(
            (target: 0m, day0: WorkCell(CellSymbol.Late, 8m), day1: WorkCell(CellSymbol.Early, 8m)),
            (target: 0m, day0: WorkCell(CellSymbol.Night, 8m), day1: FreeCell));

        var verdict = new PauseConstraintAgent().Evaluate(bitmap, new PlanCellSwap(0, 0, 1, 0, "test"));
        verdict.Vote.ShouldBe(ConstraintAgentVote.Veto);
    }

    [Test]
    public void Pause_NoTransitionAffected_Abstains()
    {
        var bitmap = BuildBitmap(
            (target: 0m, day0: WorkCell(CellSymbol.Late, 8m), day1: FreeCell),
            (target: 0m, day0: WorkCell(CellSymbol.Early, 8m), day1: FreeCell));

        var verdict = new PauseConstraintAgent().Evaluate(bitmap, new PlanCellSwap(0, 0, 1, 0, "test"));
        verdict.Vote.ShouldBe(ConstraintAgentVote.Abstain);
    }

    [Test]
    public void Consecutive_NoMaxConfigured_Abstains()
    {
        var bitmap = BuildBitmap(
            (target: 0m, max: 0, day0: WorkCell(CellSymbol.Early, 8m), day1: FreeCell),
            (target: 0m, max: 0, day0: WorkCell(CellSymbol.Late, 8m), day1: FreeCell));

        var verdict = new ConsecutiveConstraintAgent().Evaluate(bitmap, new PlanCellSwap(0, 0, 1, 0, "test"));
        verdict.Vote.ShouldBe(ConstraintAgentVote.Abstain);
    }

    [Test]
    public void Consecutive_SwapPushesRowToCap_Vetoes()
    {
        // rowA: max=3, currently F W W (run 2 ending at day 2, free at day 0).
        // rowB: day 0 = W, will be swapped onto rowA.
        // After swap: rowA = W W W (run 3 → equals max → veto).
        var bitmap = BuildBitmapNDays(
            days: 3,
            row0: (target: 0m, max: 3, cells: new[] { FreeCell, WorkCell(CellSymbol.Early, 8m), WorkCell(CellSymbol.Early, 8m) }),
            row1: (target: 0m, max: 3, cells: new[] { WorkCell(CellSymbol.Late, 8m), FreeCell, FreeCell }));

        var verdict = new ConsecutiveConstraintAgent().Evaluate(bitmap, new PlanCellSwap(0, 0, 1, 0, "test"));
        verdict.Vote.ShouldBe(ConstraintAgentVote.Veto);
    }

    [Test]
    public void Rotation_SwapCreatesThreeInARow_Vetoes()
    {
        // rowA day0 = Free, rowA day1 = Early, rowA day2 = Early.
        // rowB day0 = Early. After swap rowA = Early Early Early → 3 in a row.
        var bitmap = BuildBitmapNDays(
            days: 3,
            row0: (target: 0m, max: 0, cells: new[] { FreeCell, WorkCell(CellSymbol.Early, 8m), WorkCell(CellSymbol.Early, 8m) }),
            row1: (target: 0m, max: 0, cells: new[] { WorkCell(CellSymbol.Early, 8m), FreeCell, FreeCell }));

        var verdict = new RotationConstraintAgent().Evaluate(bitmap, new PlanCellSwap(0, 0, 1, 0, "test"));
        verdict.Vote.ShouldBe(ConstraintAgentVote.Veto);
    }

    [Test]
    public void Rotation_NoMonotoneEffect_Abstains()
    {
        var bitmap = BuildBitmap(
            (target: 0m, day0: WorkCell(CellSymbol.Early, 8m), day1: FreeCell),
            (target: 0m, day0: WorkCell(CellSymbol.Late, 8m), day1: FreeCell));

        var verdict = new RotationConstraintAgent().Evaluate(bitmap, new PlanCellSwap(0, 0, 1, 0, "test"));
        verdict.Vote.ShouldBe(ConstraintAgentVote.Abstain);
    }

    [Test]
    public void Preference_BothRowsLosePreferredShift_Vetoes()
    {
        // rowA prefers Early, currently has Early on day0. rowB prefers Late, currently has Late on day0.
        // Swap → rowA gets Late, rowB gets Early. Both lose their preference.
        var bitmap = BuildBitmapWithPrefs(
            row0: (prefs: new[] { CellSymbol.Early }, day0: WorkCell(CellSymbol.Early, 8m), day1: FreeCell),
            row1: (prefs: new[] { CellSymbol.Late }, day0: WorkCell(CellSymbol.Late, 8m), day1: FreeCell));

        var verdict = new PreferenceConstraintAgent().Evaluate(bitmap, new PlanCellSwap(0, 0, 1, 0, "test"));
        verdict.Vote.ShouldBe(ConstraintAgentVote.Veto);
    }

    [Test]
    public void Preference_BothRowsGainPreferredShift_Approves()
    {
        // rowA prefers Late, has Early. rowB prefers Early, has Late.
        // Swap → both gain.
        var bitmap = BuildBitmapWithPrefs(
            row0: (prefs: new[] { CellSymbol.Late }, day0: WorkCell(CellSymbol.Early, 8m), day1: FreeCell),
            row1: (prefs: new[] { CellSymbol.Early }, day0: WorkCell(CellSymbol.Late, 8m), day1: FreeCell));

        var verdict = new PreferenceConstraintAgent().Evaluate(bitmap, new PlanCellSwap(0, 0, 1, 0, "test"));
        verdict.Vote.ShouldBe(ConstraintAgentVote.Approve);
    }

    [Test]
    public void Preference_NoPreferenceData_Abstains()
    {
        var bitmap = BuildBitmap(
            (target: 0m, day0: WorkCell(CellSymbol.Early, 8m), day1: FreeCell),
            (target: 0m, day0: WorkCell(CellSymbol.Late, 8m), day1: FreeCell));

        var verdict = new PreferenceConstraintAgent().Evaluate(bitmap, new PlanCellSwap(0, 0, 1, 0, "test"));
        verdict.Vote.ShouldBe(ConstraintAgentVote.Abstain);
    }

    private static Cell WorkCell(CellSymbol symbol, decimal hours)
        => new(symbol, Guid.NewGuid(), [Guid.NewGuid()], IsLocked: false, Hours: hours);

    private static HarmonyBitmap BuildBitmap(
        (decimal target, Cell day0, Cell day1) row0,
        (decimal target, Cell day0, Cell day1) row1)
        => BuildBitmapNDays(days: 2,
            row0: (row0.target, max: 0, cells: new[] { row0.day0, row0.day1 }),
            row1: (row1.target, max: 0, cells: new[] { row1.day0, row1.day1 }));

    private static HarmonyBitmap BuildBitmap(
        (decimal target, int max, Cell day0, Cell day1) row0,
        (decimal target, int max, Cell day0, Cell day1) row1)
        => BuildBitmapNDays(days: 2,
            row0: (row0.target, row0.max, new[] { row0.day0, row0.day1 }),
            row1: (row1.target, row1.max, new[] { row1.day0, row1.day1 }));

    private static HarmonyBitmap BuildBitmapNDays(
        int days,
        (decimal target, int max, Cell[] cells) row0,
        (decimal target, int max, Cell[] cells) row1)
    {
        var agents = new List<BitmapAgent>
        {
            new("a-0", "Row0", row0.target, new HashSet<CellSymbol>(), MaxConsecutiveDays: row0.max),
            new("a-1", "Row1", row1.target, new HashSet<CellSymbol>(), MaxConsecutiveDays: row1.max),
        };
        var input = new BitmapInput(agents, Day0, Day0.AddDays(days - 1), []);
        var bitmap = BitmapBuilder.Build(input);
        for (var d = 0; d < days; d++)
        {
            bitmap.SetCell(0, d, row0.cells[d]);
            bitmap.SetCell(1, d, row1.cells[d]);
        }
        return bitmap;
    }

    [Test]
    public void Consecutive_BoundaryWorkExtendsRunLengthAcrossBitmapEdge_Vetoes()
    {
        // rowA: cap=3, day0=Free, day1=Work, day2=Work. Boundary entry on Day0-1 (just before bitmap)
        // is a Work. Swap moves Work onto rowA day0 → run after = boundary[Day0-1] + day0 + day1 + day2
        // = 4 days, exceeds cap of 3 → veto.
        // rowB starts with the work cell that will move to rowA day0.
        var bitmap = BuildBitmapNDays(
            days: 3,
            row0: (target: 0m, max: 3, cells: new[] { FreeCell, WorkCell(CellSymbol.Early, 8m), WorkCell(CellSymbol.Early, 8m) }),
            row1: (target: 0m, max: 3, cells: new[] { WorkCell(CellSymbol.Late, 8m), FreeCell, FreeCell }));

        var boundary = new List<BitmapAssignment>
        {
            new("a-0", Day0.AddDays(-1), CellSymbol.Early, Guid.NewGuid(),
                [Guid.NewGuid()], IsLocked: true,
                StartAt: Day0.AddDays(-1).ToDateTime(new TimeOnly(7, 0)),
                EndAt: Day0.AddDays(-1).ToDateTime(new TimeOnly(15, 0)), Hours: 8m),
        };

        var verdict = new ConsecutiveConstraintAgent(boundary).Evaluate(bitmap, new PlanCellSwap(0, 0, 1, 0, "test"));
        verdict.Vote.ShouldBe(ConstraintAgentVote.Veto);
    }

    [Test]
    public void Consecutive_NoBoundary_BackwardCompatibleWithParameterlessConstructor()
    {
        // Same in-period setup as the legacy test. Without boundary the run after = 3 (= cap exactly),
        // hits the cap → veto. Confirms the parameterless constructor still works for existing callers.
        var bitmap = BuildBitmapNDays(
            days: 3,
            row0: (target: 0m, max: 3, cells: new[] { FreeCell, WorkCell(CellSymbol.Early, 8m), WorkCell(CellSymbol.Early, 8m) }),
            row1: (target: 0m, max: 3, cells: new[] { WorkCell(CellSymbol.Late, 8m), FreeCell, FreeCell }));

        var verdict = new ConsecutiveConstraintAgent().Evaluate(bitmap, new PlanCellSwap(0, 0, 1, 0, "test"));
        verdict.Vote.ShouldBe(ConstraintAgentVote.Veto);
    }

    [Test]
    public void Pause_BoundaryNightOnDayBeforeBitmap_VetoesEarlyShiftOnDay0()
    {
        // rowA day0 = Free (will receive an Early via swap). The previous day (Day0-1, boundary) had
        // a Night shift. Swapping an Early onto rowA day0 introduces Night→Early transition across
        // the period boundary → veto.
        // rowB day0 = Early (will move onto rowA day0).
        var bitmap = BuildBitmap(
            (target: 0m, day0: FreeCell, day1: FreeCell),
            (target: 0m, day0: WorkCell(CellSymbol.Early, 8m), day1: FreeCell));

        var boundary = new List<BitmapAssignment>
        {
            new("a-0", Day0.AddDays(-1), CellSymbol.Night, Guid.NewGuid(),
                [Guid.NewGuid()], IsLocked: true,
                StartAt: Day0.AddDays(-1).ToDateTime(new TimeOnly(22, 0)),
                EndAt: Day0.ToDateTime(new TimeOnly(6, 0)), Hours: 8m),
        };

        var verdict = new PauseConstraintAgent(boundary).Evaluate(bitmap, new PlanCellSwap(0, 0, 1, 0, "test"));
        verdict.Vote.ShouldBe(ConstraintAgentVote.Veto);
    }

    [Test]
    public void Pause_NoBoundary_DefaultConstructorAbstainsWithoutTransition()
    {
        // Same setup minus the boundary night → no rough transition introduced → abstain.
        // Confirms the parameterless constructor stays backward-compatible.
        var bitmap = BuildBitmap(
            (target: 0m, day0: FreeCell, day1: FreeCell),
            (target: 0m, day0: WorkCell(CellSymbol.Early, 8m), day1: FreeCell));

        var verdict = new PauseConstraintAgent().Evaluate(bitmap, new PlanCellSwap(0, 0, 1, 0, "test"));
        verdict.Vote.ShouldBe(ConstraintAgentVote.Abstain);
    }

    private static HarmonyBitmap BuildBitmapWithPrefs(
        (CellSymbol[] prefs, Cell day0, Cell day1) row0,
        (CellSymbol[] prefs, Cell day0, Cell day1) row1)
    {
        var agents = new List<BitmapAgent>
        {
            new("a-0", "Row0", 0m, new HashSet<CellSymbol>(row0.prefs)),
            new("a-1", "Row1", 0m, new HashSet<CellSymbol>(row1.prefs)),
        };
        var input = new BitmapInput(agents, Day0, Day0.AddDays(1), []);
        var bitmap = BitmapBuilder.Build(input);
        bitmap.SetCell(0, 0, row0.day0);
        bitmap.SetCell(0, 1, row0.day1);
        bitmap.SetCell(1, 0, row1.day0);
        bitmap.SetCell(1, 1, row1.day1);
        return bitmap;
    }
}
