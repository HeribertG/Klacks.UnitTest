// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis;

/// <summary>
/// Measures the staircase picture of a plan on the grid employee x day x shift kind. The grid starts with the
/// carried-in days of the previous period, so the phase a row brings along is part of the picture. A cell is free (0) or
/// carries the latest shift kind of the day (early before late before night; the day shift reads as early), so several
/// shifts of one day are one unit. Two rows are compared under every shift of up to <see cref="MaxShiftDays"/> days
/// and the best share of equal cells counts. The value is a description of the picture, not a verdict: what number a
/// good plan reaches is calibrated against plans a planner judged by eye.
/// </summary>
public static class StaircaseHarmonyAnalyzer
{
    /// <summary>Largest shift between two rows that is tried, in days.</summary>
    public const int MaxShiftDays = 14;

    private const int EarlySymbol = 1;
    private const int LateSymbol = 2;
    private const int NightSymbol = 3;
    private const int ShuffleSeed = 20261111;
    private const int MinimumOverlapDays = 7;

    /// <param name="plan">Plan of one stage</param>
    /// <param name="definition">Scenario that produced it</param>
    public static StaircaseHarmony Analyze(CoreScenario plan, AutofillScenarioDefinition definition)
    {
        var shifts = AutofillPlanAnalyzer.BuildPlannedShifts(plan, definition, includeCarryIn: true);
        var firstDay = shifts.Values
            .SelectMany(list => list)
            .Select(shift => shift.Date)
            .Append(definition.PeriodFrom)
            .Min();
        var gridDays = definition.PeriodUntil.DayNumber - firstDay.DayNumber + 1;
        var carryInDays = definition.PeriodFrom.DayNumber - firstDay.DayNumber;

        var rows = definition.EmployeesInListOrder
            .Select(employee => RowOf(shifts[employee], firstDay, gridDays))
            .ToList();

        var random = new Random(ShuffleSeed);
        var shuffled = rows.Select(row => Shuffled(row, random)).ToList();

        var steps = new List<int>();
        for (var i = 0; i + 1 < rows.Count; i++)
        {
            steps.Add(BestShift(rows[i], rows[i + 1]).Shift);
        }

        var modalStep = steps.Count == 0
            ? 0
            : steps.GroupBy(step => step).OrderByDescending(group => group.Count()).ThenBy(group => Math.Abs(group.Key)).First().Key;
        var regularity = steps.Count == 0 ? 0 : (double)steps.Count(step => step == modalStep) / steps.Count;

        return new StaircaseHarmony(
            MeanBestAgreement(rows), MeanBestAgreement(shuffled), regularity, modalStep, gridDays, carryInDays);
    }

    private static int[] RowOf(IReadOnlyList<PlannedShift> shifts, DateOnly firstDay, int gridDays)
    {
        var row = new int[gridDays];
        foreach (var shift in shifts)
        {
            var day = shift.Date.DayNumber - firstDay.DayNumber;
            if (day >= 0 && day < gridDays)
            {
                row[day] = Math.Max(row[day], SymbolOf(shift.Kind));
            }
        }

        return row;
    }

    private static int SymbolOf(AutofillShiftKind kind) => kind switch
    {
        AutofillShiftKind.Late => LateSymbol,
        AutofillShiftKind.Night => NightSymbol,
        _ => EarlySymbol,
    };

    private static int[] Shuffled(int[] row, Random random)
    {
        var copy = (int[])row.Clone();
        random.Shuffle(copy);
        return copy;
    }

    private static double MeanBestAgreement(IReadOnlyList<int[]> rows)
    {
        var sum = 0d;
        var pairs = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            for (var j = i + 1; j < rows.Count; j++)
            {
                sum += BestShift(rows[i], rows[j]).Agreement;
                pairs++;
            }
        }

        return pairs == 0 ? 0 : sum / pairs;
    }

    private static (int Shift, double Agreement) BestShift(int[] upper, int[] lower)
    {
        var bestShift = 0;
        var bestAgreement = -1d;
        for (var magnitude = 0; magnitude <= MaxShiftDays; magnitude++)
        {
            foreach (var shift in magnitude == 0 ? new[] { 0 } : new[] { magnitude, -magnitude })
            {
                var agreement = Agreement(upper, lower, shift);
                if (agreement > bestAgreement)
                {
                    bestAgreement = agreement;
                    bestShift = shift;
                }
            }
        }

        return (bestShift, Math.Max(bestAgreement, 0));
    }

    private static double Agreement(int[] upper, int[] lower, int shift)
    {
        var equal = 0;
        var overlap = 0;
        for (var day = 0; day < upper.Length; day++)
        {
            var other = day + shift;
            if (other < 0 || other >= lower.Length)
            {
                continue;
            }

            overlap++;
            if (upper[day] == lower[other])
            {
                equal++;
            }
        }

        return overlap < MinimumOverlapDays ? -1 : (double)equal / overlap;
    }
}
