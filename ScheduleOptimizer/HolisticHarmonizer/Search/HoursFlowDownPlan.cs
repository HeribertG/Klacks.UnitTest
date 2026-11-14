// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Global;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Search;

/// <summary>
/// Two-row plan for the tests that prove the stage-3 protection is wired in: the higher-ranked row 0 works five early
/// shifts but is only due four, the lower-ranked row 1 works four and is due five. Handing the last shift of row 0 to
/// row 1 is legal for every hard rule, brings both rows closer to their target hours (so the target-hours guard
/// accepts it) and keeps both packages whole and pure; only the protection refuses it, because the higher rank would lose
/// hours to the lower one.
/// </summary>
internal static class HoursFlowDownPlan
{
    public const int HigherRow = 0;
    public const int LowerRow = 1;
    public const int LastWorkedDay = 4;
    public const string HigherRowText = "EEEEE";
    public const string LowerRowText = "EEEE_";

    private const decimal ShiftHours = 8m;
    private const decimal HigherRowTargetShifts = 4m;
    private const decimal LowerRowTargetShifts = 5m;
    private const string StepReason = "hands the last shift down";

    public static BitmapInput Input()
    {
        var input = HarmonyTestPlans.Input(HigherRowText, LowerRowText);
        var agents = input.Agents
            .Select((agent, row) => agent with
            {
                TargetHours = (row == HigherRow ? HigherRowTargetShifts : LowerRowTargetShifts) * ShiftHours,
            })
            .ToList();
        return input with { Agents = agents };
    }

    public static PlanCellSwap HandDownStep() => new(HigherRow, LastWorkedDay, LowerRow, LastWorkedDay, StepReason);

    public static int WorkedDays(HarmonyBitmap bitmap, int row)
        => HarmonyTestPlans.Row(bitmap, row).Count(symbol => symbol != '_');
}
