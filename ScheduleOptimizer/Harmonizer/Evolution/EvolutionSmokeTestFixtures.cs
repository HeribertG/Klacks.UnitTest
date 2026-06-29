// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Evolution;

/// <summary>
/// Shared deterministic bitmap fixtures for the evolution/annealing smoke tests so the GA and
/// SA suites optimise the exact same seed state and cannot drift apart.
/// </summary>
internal static class EvolutionSmokeTestFixtures
{
    public static HarmonyBitmap BuildChaoticBitmap()
    {
        var agents = new List<BitmapAgent>
        {
            new("agent-0", "A0", 200m, new HashSet<CellSymbol>{ CellSymbol.Early }),
            new("agent-1", "A1", 150m, new HashSet<CellSymbol>{ CellSymbol.Late }),
            new("agent-2", "A2", 100m, new HashSet<CellSymbol>{ CellSymbol.Night }),
        };
        var startDate = new DateOnly(2026, 1, 1);
        var assignments = new List<BitmapAssignment>();
        var rng = new Random(99);
        var palette = new[] { CellSymbol.Early, CellSymbol.Late, CellSymbol.Night };
        for (var r = 0; r < agents.Count; r++)
        {
            for (var d = 0; d < 7; d++)
            {
                if (rng.NextDouble() < 0.6)
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
        var input = new BitmapInput(agents, startDate, startDate.AddDays(6), assignments);
        return BitmapBuilder.Build(input);
    }
}
