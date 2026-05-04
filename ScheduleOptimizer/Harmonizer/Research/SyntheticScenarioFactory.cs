// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/**
 * Generates deterministic synthetic harmonizer scenarios from a seed. Used to keep the
 * research loop honest: a single real plan can be overfitted; running the loop against
 * multiple profiles guards against scoring goodhart.
 *
 * Profiles:
 *   - Tiny: 3 agents x 14 days, sanity-check
 *   - Mid:  10 agents x 28 days, mid load
 *   - Heavy: 20 agents x 42 days, scaling
 *   - EdgeCases: 5 agents x 14 days, blacklist + uneven contracts
 */

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Research;

public static class SyntheticScenarioFactory
{
    private const int FillRatePercent = 50;
    private const decimal ShiftHours = 8m;
    private static readonly TimeOnly[] StartTimes =
    {
        new(6, 0),
        new(14, 0),
        new(22, 0),
    };

    public static (ScenarioSnapshot Snapshot, BitmapInput Input) Tiny(int seed = 1)
        => Build("synthetic-tiny-3x14", agents: 3, days: 14, seed, maxConsec: 6);

    public static (ScenarioSnapshot Snapshot, BitmapInput Input) Mid(int seed = 2)
        => Build("synthetic-mid-10x28", agents: 10, days: 28, seed, maxConsec: 6);

    public static (ScenarioSnapshot Snapshot, BitmapInput Input) Heavy(int seed = 3)
        => Build("synthetic-heavy-20x42", agents: 20, days: 42, seed, maxConsec: 6);

    public static (ScenarioSnapshot Snapshot, BitmapInput Input) EdgeCases(int seed = 4)
        => Build("synthetic-edge-5x14", agents: 5, days: 14, seed, maxConsec: 5);

    private static (ScenarioSnapshot, BitmapInput) Build(
        string name,
        int agents,
        int days,
        int seed,
        int maxConsec)
    {
        var rng = new Random(seed);
        var fromDate = new DateOnly(2026, 1, 5);
        var untilDate = fromDate.AddDays(days - 1);

        var agentList = new List<ScenarioAgent>(agents);
        var agentIds = new List<Guid>(agents);
        for (var i = 0; i < agents; i++)
        {
            agentIds.Add(Guid.NewGuid());
            agentList.Add(new ScenarioAgent(
                Id: agentIds[i].ToString(),
                DisplayName: $"Agent {i + 1:00}",
                TargetHours: days * ShiftHours * 0.6m));
        }

        var shiftIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        var assignments = new List<ScenarioAssignment>();
        for (var d = 0; d < days; d++)
        {
            var date = fromDate.AddDays(d);
            for (var a = 0; a < agents; a++)
            {
                if (rng.Next(100) >= FillRatePercent)
                {
                    continue;
                }
                var shiftIdx = rng.Next(StartTimes.Length);
                var start = StartTimes[shiftIdx];
                var end = start.AddHours(8);
                assignments.Add(new ScenarioAssignment(
                    AgentId: agentIds[a].ToString(),
                    Date: date.ToString("yyyy-MM-dd"),
                    ShiftId: shiftIds[shiftIdx].ToString(),
                    StartTime: start.ToString("HH:mm:ss"),
                    EndTime: end.ToString("HH:mm:ss"),
                    Hours: ShiftHours));
            }
        }

        var snapshot = new ScenarioSnapshot(
            Name: name,
            FromDate: fromDate.ToString("yyyy-MM-dd"),
            UntilDate: untilDate.ToString("yyyy-MM-dd"),
            MaxConsecutiveDays: maxConsec,
            MaxWeeklyHours: 40m,
            Agents: agentList,
            Assignments: assignments);

        var input = ScenarioLoader.ToBitmapInput(snapshot);
        return (snapshot, input);
    }
}
