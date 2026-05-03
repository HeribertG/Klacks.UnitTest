// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Conductor;

[TestFixture]
public class HarmonizerConductorSmokeTests
{
    [Test]
    public void Run_OnSmallBitmap_DoesNotThrow_AndProducesTracePerRow()
    {
        var bitmap = BuildChaoticBitmap();
        var conductor = BuildConductor(bitmap.RowCount);

        var result = conductor.Run(bitmap);

        result.RowTraces.Count.ShouldBe(bitmap.RowCount);
        result.InitialRowScores.Count.ShouldBe(bitmap.RowCount);
        result.FinalRowScores.Count.ShouldBe(bitmap.RowCount);
        for (var r = 0; r < bitmap.RowCount; r++)
        {
            result.RowTraces[r].RowIndex.ShouldBe(r);
        }
    }

    [Test]
    public void Run_LockedCells_AreNotMutated()
    {
        var bitmap = BuildChaoticBitmap();
        var lockedCell = new Cell(CellSymbol.Night, Guid.NewGuid(), [Guid.NewGuid()], true);
        bitmap.SetCell(0, 0, lockedCell);
        var conductor = BuildConductor(bitmap.RowCount);

        conductor.Run(bitmap);

        bitmap.GetCell(0, 0).ShouldBe(lockedCell);
    }

    [Test]
    public void Run_TopDownProcessing_LocksUpperRowsBeforeLower()
    {
        var bitmap = BuildChaoticBitmap();
        var conductor = BuildConductor(bitmap.RowCount);

        var result = conductor.Run(bitmap);

        result.RowTraces[0].RowIndex.ShouldBeLessThan(result.RowTraces[^1].RowIndex);
    }

    private static HarmonizerConductor BuildConductor(int rowCount)
    {
        var scorer = new HarmonyScorer();
        var validator = new BitmapReplaceValidator();
        var mutation = new ReplaceMutation(scorer, validator);
        var emergencyState = new EmergencyUnlockState(rowCount);
        var emergency = new EmergencyUnlockManager(emergencyState);
        return new HarmonizerConductor(scorer, mutation, emergency);
    }

    private static HarmonyBitmap BuildChaoticBitmap()
    {
        var agents = new List<BitmapAgent>
        {
            new("agent-0", "Top Soll", 200m, new HashSet<CellSymbol>{ CellSymbol.Early }),
            new("agent-1", "Mid Soll", 150m, new HashSet<CellSymbol>{ CellSymbol.Late }),
            new("agent-2", "Low Soll", 100m, new HashSet<CellSymbol>{ CellSymbol.Night }),
        };
        var startDate = new DateOnly(2026, 1, 1);
        var assignments = new List<BitmapAssignment>
        {
            new("agent-0", startDate.AddDays(0), CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], false),
            new("agent-0", startDate.AddDays(1), CellSymbol.Night, Guid.NewGuid(), [Guid.NewGuid()], false),
            new("agent-0", startDate.AddDays(3), CellSymbol.Late, Guid.NewGuid(), [Guid.NewGuid()], false),
            new("agent-1", startDate.AddDays(0), CellSymbol.Late, Guid.NewGuid(), [Guid.NewGuid()], false),
            new("agent-1", startDate.AddDays(2), CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], false),
            new("agent-1", startDate.AddDays(4), CellSymbol.Night, Guid.NewGuid(), [Guid.NewGuid()], false),
            new("agent-2", startDate.AddDays(1), CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], false),
            new("agent-2", startDate.AddDays(2), CellSymbol.Night, Guid.NewGuid(), [Guid.NewGuid()], false),
            new("agent-2", startDate.AddDays(4), CellSymbol.Late, Guid.NewGuid(), [Guid.NewGuid()], false),
        };
        var input = new BitmapInput(agents, startDate, startDate.AddDays(6), assignments);
        return BitmapBuilder.Build(input);
    }
}
