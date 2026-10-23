// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;

namespace Klacks.UnitTest.Application.Services.Schedules.HolisticHarmonizer.Critic;

/// <summary>
/// Small fixed plan for the vision-critic tests: two agents with real names over three days (Sat 24 - Mon 26 Oct
/// 2026). r00 works night then a locked early, r01 has a locked absence on day 2; every other cell is free.
/// </summary>
internal static class PlanCriticTestPlans
{
    public const string FirstAgentName = "Anna Muster";
    public const string SecondAgentName = "Beat Beispiel";

    public static HarmonyBitmap TwoAgentsThreeDays()
    {
        var rows = new[]
        {
            new BitmapAgent("a1", FirstAgentName, 40m, new HashSet<CellSymbol>()),
            new BitmapAgent("a2", SecondAgentName, 40m, new HashSet<CellSymbol>()),
        };
        var days = Enumerable.Range(0, 3).Select(i => new DateOnly(2026, 10, 24).AddDays(i)).ToArray();
        var cells = new Cell[2, 3];
        for (var row = 0; row < 2; row++)
        {
            for (var day = 0; day < 3; day++)
            {
                cells[row, day] = Cell.Free();
            }
        }

        cells[0, 0] = new Cell(CellSymbol.Night, Guid.NewGuid(), [], false);
        cells[0, 1] = new Cell(CellSymbol.Early, Guid.NewGuid(), [], true);
        cells[1, 2] = new Cell(CellSymbol.Break, null, [], true);
        return new HarmonyBitmap(rows, days, cells);
    }
}
