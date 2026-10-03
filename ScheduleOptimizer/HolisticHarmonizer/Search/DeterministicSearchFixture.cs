// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Search;

/// <summary>
/// Synthetic, seeded stage-3 input: six employees over three Monday-to-Sunday weeks, every day one early,
/// one late and one night shift handed to three random distinct employees. Uneven targets and the random
/// spread leave room for improving swaps without any real planning engine.
/// </summary>
internal static class DeterministicSearchFixture
{
    public const int EmployeeCount = 6;
    public const int DayCount = 21;
    public const decimal ShiftHours = 8m;
    public const int MaxConsecutiveDays = 6;
    public const decimal MinPauseHours = 11m;
    public const decimal MaxWeeklyHours = 50m;
    public const int MinRestDays = 1;

    private const int EarlyStartHour = 7;
    private const int LateStartHour = 15;
    private const int NightStartHour = 23;
    private const int FirstTargetHours = 60;
    private const int TargetHoursStep = 10;

    private static readonly DateOnly From = new(2026, 3, 2);
    private static readonly Guid EarlyShift = new("00000000-0000-0000-0000-0000000000e1");
    private static readonly Guid LateShift = new("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid NightShift = new("00000000-0000-0000-0000-0000000000c3");

    public static BitmapInput BuildInput(int seed)
    {
        var random = new Random(seed);
        var agents = Enumerable.Range(0, EmployeeCount)
            .Select(i => new BitmapAgent(
                Id: $"MA-{i + 1}",
                DisplayName: $"MA-{i + 1}",
                TargetHours: FirstTargetHours + i * TargetHoursStep,
                PreferredShiftSymbols: new HashSet<CellSymbol>(),
                MaxWeeklyHours: MaxWeeklyHours,
                MaxConsecutiveDays: MaxConsecutiveDays,
                MinPauseHours: MinPauseHours,
                MinRestDays: MinRestDays))
            .ToList();

        var shifts = new[]
        {
            (CellSymbol.Early, EarlyShift, EarlyStartHour),
            (CellSymbol.Late, LateShift, LateStartHour),
            (CellSymbol.Night, NightShift, NightStartHour),
        };

        var assignments = new List<BitmapAssignment>();
        for (var d = 0; d < DayCount; d++)
        {
            var date = From.AddDays(d);
            var order = Enumerable.Range(0, EmployeeCount).OrderBy(_ => random.Next()).ToList();
            for (var s = 0; s < shifts.Length; s++)
            {
                var (symbol, shiftId, startHour) = shifts[s];
                var start = date.ToDateTime(new TimeOnly(startHour, 0), DateTimeKind.Utc);
                assignments.Add(new BitmapAssignment(
                    AgentId: agents[order[s]].Id,
                    Date: date,
                    Symbol: symbol,
                    ShiftRefId: shiftId,
                    WorkIds: [NewGuid(random)],
                    IsLocked: false,
                    StartAt: start,
                    EndAt: start.AddHours((double)ShiftHours),
                    Hours: ShiftHours));
            }
        }

        return new BitmapInput(agents, From, From.AddDays(DayCount - 1), assignments);
    }

    public static HarmonyBitmap BuildBitmap(BitmapInput input) => RowSorter.Sort(BitmapBuilder.Build(input));

    private static Guid NewGuid(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }
}
