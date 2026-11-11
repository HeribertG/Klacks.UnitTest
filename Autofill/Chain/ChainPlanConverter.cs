// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.Autofill.Chain;

/// <summary>
/// Turns a bitmap stage result back into works, the way production re-points saved works: every non-free cell carries
/// exactly one work id, and the work gets the cell's row as its new holder while day, shift, times and hours stay those
/// of the work. Fails loudly on anything that would make the round trip lossy: a cell without or with several work ids,
/// an unknown work id, a work moved to another day (the measured stages swap within a day only), changed times, or a
/// different number of works than the stage started with.
/// </summary>
public static class ChainPlanConverter
{
    /// <summary>The works of a stage result, in the order of the works the stage started from.</summary>
    /// <param name="bitmap">Bitmap after the stage</param>
    /// <param name="before">Works the stage started from</param>
    public static IReadOnlyList<ChainWork> WorksOf(HarmonyBitmap bitmap, IReadOnlyList<ChainWork> before)
    {
        var byId = before.ToDictionary(w => w.WorkId);
        var position = before.Select((w, index) => (w.WorkId, index)).ToDictionary(p => p.WorkId, p => p.index);
        var after = new List<ChainWork>(before.Count);
        var problems = new List<string>();

        for (var row = 0; row < bitmap.RowCount; row++)
        {
            for (var dayIndex = 0; dayIndex < bitmap.DayCount; dayIndex++)
            {
                var cell = bitmap.GetCell(row, dayIndex);
                if (cell.Symbol == CellSymbol.Free)
                {
                    continue;
                }

                var day = bitmap.Days[dayIndex];
                var agentId = bitmap.Rows[row].Id;
                if (cell.WorkIds.Count != 1 || !byId.TryGetValue(cell.WorkIds[0], out var work))
                {
                    problems.Add($"{agentId} {day:yyyy-MM-dd}: cell holds {cell.WorkIds.Count} work id(s) that do not map to exactly one work");
                    continue;
                }

                if (work.Token.Date != day || cell.StartAt != work.Token.StartAt || cell.EndAt != work.Token.EndAt)
                {
                    problems.Add($"{agentId} {day:yyyy-MM-dd}: work of {work.Token.Date:yyyy-MM-dd} {work.Token.StartAt:HH:mm} arrived moved or re-timed");
                    continue;
                }

                after.Add(work with { Token = work.Token with { AgentId = agentId } });
            }
        }

        if (after.Count != before.Count || after.Select(w => w.WorkId).Distinct().Count() != before.Count)
        {
            problems.Add($"the stage started with {before.Count} works and ended with {after.Count} cells");
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "The bitmap stage result cannot be mapped back onto its works: " + string.Join("; ", problems));
        }

        return after.OrderBy(w => position[w.WorkId]).ToList();
    }

    /// <summary>The works as a plan the analyzer reads.</summary>
    /// <param name="works">Works of one stage</param>
    public static CoreScenario ScenarioOf(IReadOnlyList<ChainWork> works)
        => new() { Tokens = works.Select(w => w.Token).ToList() };
}
