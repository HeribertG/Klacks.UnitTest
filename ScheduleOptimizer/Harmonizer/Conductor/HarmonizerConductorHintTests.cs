// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Conductor;

[TestFixture]
public class HarmonizerConductorHintTests
{
    [Test]
    public void Run_RowWithHint_GetsExtendedIterationBudget()
    {
        var bitmap = BuildBitmap();
        var hints = new List<SofteningHint>
        {
            new("agent-0", new DateOnly(2026, 1, 1), SofteningKind.MinRestDays, ["MinRestDays"]),
        };

        var withoutHints = RunConductor(bitmap, null, maxIterationsPerRow: 4, multiplier: 8);
        var freshBitmap = BuildBitmap();
        var withHints = RunConductor(freshBitmap, hints, maxIterationsPerRow: 4, multiplier: 8);

        withHints.RowTraces[0].MovesApplied.ShouldBeGreaterThanOrEqualTo(withoutHints.RowTraces[0].MovesApplied);
    }

    [Test]
    public void Run_NoHints_BehavesIdenticallyToOldConductor()
    {
        var bitmap = BuildBitmap();
        var withEmpty = RunConductor(bitmap, [], maxIterationsPerRow: 4, multiplier: 8);
        var freshBitmap = BuildBitmap();
        var withNull = RunConductor(freshBitmap, null, maxIterationsPerRow: 4, multiplier: 8);

        withEmpty.RowTraces.Count.ShouldBe(withNull.RowTraces.Count);
        for (var r = 0; r < withEmpty.RowTraces.Count; r++)
        {
            withEmpty.RowTraces[r].MovesApplied.ShouldBe(withNull.RowTraces[r].MovesApplied);
        }
    }

    private static ConductorResult RunConductor(
        HarmonyBitmap bitmap,
        IReadOnlyList<SofteningHint>? hints,
        int maxIterationsPerRow,
        int multiplier)
    {
        var scorer = new HarmonyScorer();
        var validator = new BitmapReplaceValidator();
        var mutation = new ReplaceMutation(scorer, validator);
        var emergency = new EmergencyUnlockManager(new EmergencyUnlockState(bitmap.RowCount));
        var conductor = new HarmonizerConductor(scorer, mutation, emergency, maxIterationsPerRow, hints, multiplier);
        return conductor.Run(bitmap);
    }

    private static HarmonyBitmap BuildBitmap()
    {
        var agents = new List<BitmapAgent>
        {
            new("agent-0", "Top", 200m, new HashSet<CellSymbol>()),
            new("agent-1", "Mid", 150m, new HashSet<CellSymbol>()),
            new("agent-2", "Low", 100m, new HashSet<CellSymbol>()),
        };
        var startDate = new DateOnly(2026, 1, 1);
        var palette = new[] { CellSymbol.Early, CellSymbol.Late, CellSymbol.Night };
        var assignments = new List<BitmapAssignment>();
        var rng = new Random(11);
        for (var r = 0; r < agents.Count; r++)
        {
            for (var d = 0; d < 12; d++)
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
        var input = new BitmapInput(agents, startDate, startDate.AddDays(11), assignments);
        return BitmapBuilder.Build(input);
    }
}
