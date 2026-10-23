// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Global;

/// <summary>
/// Builds small stage-3 inputs from text rows, one character per day: E early (07-15), L late (15-23), N night
/// (23-07), B absence (locked), _ free. Agents: max. 6 consecutive days, 11 h minimum pause, 50 h per week, target =
/// worked hours of the row, so a plan starts exactly on target. Starts on a Monday.
/// </summary>
internal static class HarmonyTestPlans
{
    public static readonly DateOnly From = new(2026, 11, 2);
    public static readonly Guid EarlyShift = new("00000000-0000-0000-0000-0000000000e1");
    public static readonly Guid LateShift = new("00000000-0000-0000-0000-0000000000a2");
    public static readonly Guid NightShift = new("00000000-0000-0000-0000-0000000000c3");

    private const decimal ShiftHours = 8m;

    public static BitmapInput Input(params string[] rows)
    {
        var agents = rows.Select((row, i) => new BitmapAgent(
            Id: $"MA{i + 1:00}",
            DisplayName: $"MA{i + 1:00}",
            TargetHours: row.Count(c => c is 'E' or 'L' or 'N') * ShiftHours,
            PreferredShiftSymbols: new HashSet<CellSymbol>(),
            MaxWeeklyHours: 50m,
            MaxConsecutiveDays: 6,
            MinPauseHours: 11m,
            MinRestDays: 1)).ToList();

        var random = new Random(7);
        var assignments = new List<BitmapAssignment>();
        for (var r = 0; r < rows.Length; r++)
        {
            for (var d = 0; d < rows[r].Length; d++)
            {
                var date = From.AddDays(d);
                var (symbol, shift, hour) = rows[r][d] switch
                {
                    'E' => (CellSymbol.Early, EarlyShift, 7),
                    'L' => (CellSymbol.Late, LateShift, 15),
                    'N' => (CellSymbol.Night, NightShift, 23),
                    'B' => (CellSymbol.Break, Guid.Empty, 0),
                    _ => (CellSymbol.Free, Guid.Empty, 0),
                };
                if (symbol == CellSymbol.Free)
                {
                    continue;
                }

                var start = date.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Utc);
                var bytes = new byte[16];
                random.NextBytes(bytes);
                assignments.Add(new BitmapAssignment(
                    AgentId: agents[r].Id,
                    Date: date,
                    Symbol: symbol,
                    ShiftRefId: shift,
                    WorkIds: [new Guid(bytes)],
                    IsLocked: symbol == CellSymbol.Break,
                    StartAt: start,
                    EndAt: start.AddHours((double)ShiftHours),
                    Hours: symbol == CellSymbol.Break ? 0m : ShiftHours));
            }
        }

        return new BitmapInput(agents, From, From.AddDays(rows[0].Length - 1), assignments);
    }

    /// <summary>Bitmap in input row order (no RowSorter), so tests can address rows by their text index.</summary>
    public static HarmonyBitmap Bitmap(params string[] rows) => BitmapBuilder.Build(Input(rows));

    public static string Row(HarmonyBitmap bitmap, int row)
        => string.Concat(Enumerable.Range(0, bitmap.DayCount).Select(d => bitmap.GetCell(row, d).Symbol switch
        {
            CellSymbol.Early => 'E',
            CellSymbol.Late => 'L',
            CellSymbol.Night => 'N',
            CellSymbol.Break => 'B',
            CellSymbol.Other => 'O',
            _ => '_',
        }));
}
