// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/**
 * External metrics of the stage-3 benchmark. Every arm's final plan is first rebuilt with PRORATED agent
 * targets and the same row order, so the raw-target arm is not judged by its own (wrong) yardstick:
 *   - TargetDeviationHours: sum over rows of |worked hours - prorated target| (worked = cell hours, WorkTime);
 *     under structural undersupply this sum is pinned by coverage, so TargetDeviationRms (root mean square per
 *     row) and MaxRowDeviationHours show how evenly the shortfall is spread
 *   - HardViolations: independent whole-plan recount of the hard rules the evaluator enforces per swap
 *     (MaxConsecutiveDays runs, MinPauseHours between adjacent shifts, MaxWeeklyHours and MinRestDays per
 *     Monday-to-Sunday week; weeks only partly inside the range are skipped for the weekly rules)
 *   - CorrectedFitness: production HarmonyFitnessEvaluator on the rebuilt (prorated) bitmap
 */

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Benchmark;

public sealed record Stage3Metrics(
    decimal TargetDeviationHours,
    double TargetDeviationRms,
    decimal MaxRowDeviationHours,
    int HardViolations,
    int ConsecutiveViolations,
    int PauseViolations,
    int WeeklyHoursViolations,
    int RestDayViolations,
    double CorrectedFitness,
    int WorkCells);

public static class Stage3BenchmarkMetrics
{
    private const int DaysPerWeek = 7;

    public static Stage3Metrics Measure(HarmonyBitmap finalBitmap, Stage3Scenario scenario)
    {
        var rebuilt = RowSorter.Sort(BitmapBuilder.Build(
            Stage3BenchmarkPipeline.Rebuild(finalBitmap, scenario, Stage3TargetMode.Prorated)));

        var deviation = 0m;
        var squared = 0.0;
        var maxRow = 0m;
        var consecutive = 0;
        var pause = 0;
        var weeklyHours = 0;
        var restDays = 0;
        var workCells = 0;
        for (var r = 0; r < rebuilt.RowCount; r++)
        {
            var agent = rebuilt.Rows[r];
            var worked = 0m;
            for (var d = 0; d < rebuilt.DayCount; d++)
            {
                var cell = rebuilt.GetCell(r, d);
                worked += cell.Hours;
                if (IsWork(cell.Symbol))
                {
                    workCells++;
                }
            }
            var rowDeviation = Math.Abs(worked - agent.TargetHours);
            deviation += rowDeviation;
            squared += (double)(rowDeviation * rowDeviation);
            maxRow = Math.Max(maxRow, rowDeviation);
            consecutive += CountConsecutiveViolations(rebuilt, r, agent.MaxConsecutiveDays);
            pause += CountPauseViolations(rebuilt, r, agent.MinPauseHours);
            (var weekly, var rest) = CountWeeklyViolations(rebuilt, r, agent);
            weeklyHours += weekly;
            restDays += rest;
        }

        var fitness = new HarmonyFitnessEvaluator(new HarmonyScorer()).Evaluate(rebuilt).Fitness;
        return new Stage3Metrics(
            deviation,
            Math.Sqrt(squared / Math.Max(1, rebuilt.RowCount)),
            maxRow,
            consecutive + pause + weeklyHours + restDays,
            consecutive,
            pause,
            weeklyHours,
            restDays,
            fitness,
            workCells);
    }

    /// <summary>Whole-plan recount of the hard rules (consecutive days, pause, weekly hours, weekly rest days).</summary>
    public static int CountHardViolations(HarmonyBitmap bitmap)
    {
        var total = 0;
        for (var r = 0; r < bitmap.RowCount; r++)
        {
            var agent = bitmap.Rows[r];
            var (weekly, rest) = CountWeeklyViolations(bitmap, r, agent);
            total += CountConsecutiveViolations(bitmap, r, agent.MaxConsecutiveDays)
                + CountPauseViolations(bitmap, r, agent.MinPauseHours)
                + weekly
                + rest;
        }
        return total;
    }

    private static int CountConsecutiveViolations(HarmonyBitmap bitmap, int row, int cap)
    {
        if (cap <= 0)
        {
            return 0;
        }
        var violations = 0;
        var run = 0;
        for (var d = 0; d < bitmap.DayCount; d++)
        {
            run = IsWork(bitmap.GetCell(row, d).Symbol) ? run + 1 : 0;
            if (run == cap + 1)
            {
                violations++;
            }
        }
        return violations;
    }

    private static int CountPauseViolations(HarmonyBitmap bitmap, int row, decimal minPauseHours)
    {
        if (minPauseHours <= 0)
        {
            return 0;
        }
        var violations = 0;
        for (var d = 1; d < bitmap.DayCount; d++)
        {
            var previous = bitmap.GetCell(row, d - 1);
            var current = bitmap.GetCell(row, d);
            if (!IsWork(previous.Symbol) || !IsWork(current.Symbol)
                || previous.EndAt == default || current.StartAt == default)
            {
                continue;
            }
            if ((decimal)(current.StartAt - previous.EndAt).TotalHours < minPauseHours)
            {
                violations++;
            }
        }
        return violations;
    }

    private static (int WeeklyHours, int RestDays) CountWeeklyViolations(HarmonyBitmap bitmap, int row, BitmapAgent agent)
    {
        var weeklyHours = 0;
        var restDays = 0;
        for (var start = 0; start + DaysPerWeek <= bitmap.DayCount; start++)
        {
            if (bitmap.Days[start].DayOfWeek != DayOfWeek.Monday)
            {
                continue;
            }
            var hours = 0m;
            var free = 0;
            for (var d = start; d < start + DaysPerWeek; d++)
            {
                var cell = bitmap.GetCell(row, d);
                if (IsWork(cell.Symbol))
                {
                    hours += cell.Hours;
                }
                else
                {
                    free++;
                }
            }
            if (agent.MaxWeeklyHours > 0 && hours > agent.MaxWeeklyHours)
            {
                weeklyHours++;
            }
            if (agent.MinRestDays > 0 && free < agent.MinRestDays)
            {
                restDays++;
            }
        }
        return (weeklyHours, restDays);
    }

    private static bool IsWork(CellSymbol symbol)
        => symbol is CellSymbol.Early or CellSymbol.Late or CellSymbol.Night or CellSymbol.Other;
}
