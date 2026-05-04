// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/**
 * Computes objective quality metrics over a HarmonyBitmap for the harmonizer research loop.
 * Metrics are domain-driven (block fragmentation, consecutive-day violations, target-hours
 * fairness) and independent of the HarmonyScorer so the loop can detect scoring lies.
 */

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Research;

public sealed record ScheduleQualityReport(
    int TotalBlocks,
    double AvgBlockLength,
    int MaxConsecutiveDays,
    int ConsecutiveDayViolations,
    decimal TargetHoursStdDev,
    decimal TargetHoursAbsoluteDeviation,
    IReadOnlyList<RowQuality> Rows);

public sealed record RowQuality(
    string AgentId,
    string DisplayName,
    int WorkDays,
    int Blocks,
    double AvgBlockLength,
    int MaxConsecutive,
    int Violations,
    decimal AssignedHours,
    decimal TargetHours);

public static class ScheduleQualityMetrics
{
    public static ScheduleQualityReport Compute(HarmonyBitmap bitmap)
    {
        var rows = new List<RowQuality>(bitmap.RowCount);
        for (var r = 0; r < bitmap.RowCount; r++)
        {
            rows.Add(ComputeRow(bitmap, r));
        }

        var totalBlocks = rows.Sum(r => r.Blocks);
        var blocksAvg = totalBlocks == 0
            ? 0.0
            : rows.Where(r => r.Blocks > 0)
                  .Sum(r => r.AvgBlockLength * r.Blocks) / totalBlocks;
        var maxConsec = rows.Count == 0 ? 0 : rows.Max(r => r.MaxConsecutive);
        var violations = rows.Sum(r => r.Violations);

        var fairness = ComputeFairness(rows);

        return new ScheduleQualityReport(
            TotalBlocks: totalBlocks,
            AvgBlockLength: blocksAvg,
            MaxConsecutiveDays: maxConsec,
            ConsecutiveDayViolations: violations,
            TargetHoursStdDev: fairness.StdDev,
            TargetHoursAbsoluteDeviation: fairness.SumAbsDeviation,
            Rows: rows);
    }

    private static RowQuality ComputeRow(HarmonyBitmap bitmap, int rowIndex)
    {
        var agent = bitmap.Rows[rowIndex];
        var blocks = new List<int>();
        var run = 0;
        var maxConsec = 0;
        var workDays = 0;
        decimal assignedHours = 0m;
        var maxAllowed = agent.MaxConsecutiveDays > 0 ? agent.MaxConsecutiveDays : 6;
        var violations = 0;

        for (var d = 0; d < bitmap.DayCount; d++)
        {
            var cell = bitmap.GetCell(rowIndex, d);
            if (cell.Symbol == CellSymbol.Free)
            {
                if (run > 0) { blocks.Add(run); }
                run = 0;
                continue;
            }
            run++;
            workDays++;
            assignedHours += cell.Hours;
            if (run > maxConsec) { maxConsec = run; }
            if (run > maxAllowed) { violations++; }
        }
        if (run > 0) { blocks.Add(run); }

        var avg = blocks.Count == 0 ? 0.0 : blocks.Average();
        return new RowQuality(
            AgentId: agent.Id,
            DisplayName: agent.DisplayName,
            WorkDays: workDays,
            Blocks: blocks.Count,
            AvgBlockLength: avg,
            MaxConsecutive: maxConsec,
            Violations: violations,
            AssignedHours: assignedHours,
            TargetHours: agent.TargetHours);
    }

    private static (decimal StdDev, decimal SumAbsDeviation) ComputeFairness(IReadOnlyList<RowQuality> rows)
    {
        if (rows.Count == 0)
        {
            return (0m, 0m);
        }
        var deviations = rows.Select(r => r.AssignedHours - r.TargetHours).ToList();
        var sumAbs = deviations.Sum(Math.Abs);
        var meanDev = (double)deviations.Average();
        var variance = deviations.Average(d => Math.Pow((double)d - meanDev, 2));
        return ((decimal)Math.Sqrt(variance), sumAbs);
    }
}
