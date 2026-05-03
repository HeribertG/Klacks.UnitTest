// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Conductor;

[TestFixture]
public class HarmonizerConductorAntiOscillationTests
{
    [Test]
    public void Run_TopRowFinalState_DoesNotChangeAfterItsTurn()
    {
        var bitmap = BuildRichBitmap();
        var conductor = BuildConductor(bitmap.RowCount);

        var result = conductor.Run(bitmap);

        var topTrace = result.RowTraces[0];
        topTrace.RowIndex.ShouldBe(0);
        result.FinalRowScores[0].ShouldBe(topTrace.ScoreAfter, 1e-9);
    }

    [Test]
    public void Run_LockedCell_NeverChangesSymbol()
    {
        var bitmap = BuildRichBitmap();
        var lockedCell = new Cell(CellSymbol.Night, Guid.NewGuid(), [Guid.NewGuid()], true);
        bitmap.SetCell(2, 1, lockedCell);
        var conductor = BuildConductor(bitmap.RowCount);

        conductor.Run(bitmap);

        bitmap.GetCell(2, 1).ShouldBe(lockedCell);
    }

    [Test]
    public void Run_RowsTracedInProcessingOrder()
    {
        var bitmap = BuildRichBitmap();
        var conductor = BuildConductor(bitmap.RowCount);

        var result = conductor.Run(bitmap);

        for (var i = 0; i < result.RowTraces.Count; i++)
        {
            result.RowTraces[i].RowIndex.ShouldBe(i);
        }
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

    private static HarmonyBitmap BuildRichBitmap()
    {
        var agents = new List<BitmapAgent>
        {
            new("a", "A", 200m, new HashSet<CellSymbol> { CellSymbol.Early }),
            new("b", "B", 150m, new HashSet<CellSymbol> { CellSymbol.Late }),
            new("c", "C", 100m, new HashSet<CellSymbol> { CellSymbol.Night }),
        };
        var startDate = new DateOnly(2026, 1, 1);
        var palette = new[] { CellSymbol.Early, CellSymbol.Late, CellSymbol.Night };
        var assignments = new List<BitmapAssignment>();
        var rng = new Random(123);
        for (var r = 0; r < agents.Count; r++)
        {
            for (var d = 0; d < 10; d++)
            {
                if (rng.NextDouble() < 0.7)
                {
                    assignments.Add(new BitmapAssignment(
                        agents[r].Id,
                        startDate.AddDays(d),
                        palette[rng.Next(palette.Length)],
                        Guid.NewGuid(),
                        [Guid.NewGuid()],
                        false));
                }
            }
        }
        var input = new BitmapInput(agents, startDate, startDate.AddDays(9), assignments);
        return BitmapBuilder.Build(input);
    }
}
