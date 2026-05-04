// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/**
 * Loads a ScenarioSnapshot JSON from the Scenarios folder and converts it into the
 * harmonizer's BitmapInput. CellSymbol is derived from start time (same rule as production
 * HarmonizerContextBuilder.SymbolFromStartTime).
 */

using System.Text.Json;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Research;

public static class ScenarioLoader
{
    private const string ScenariosFolder = "ScheduleOptimizer/Harmonizer/Research/Scenarios";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static (ScenarioSnapshot Snapshot, BitmapInput Input) Load(string fileName)
    {
        var path = ResolveScenarioPath(fileName);
        var json = File.ReadAllText(path);
        var snapshot = JsonSerializer.Deserialize<ScenarioSnapshot>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize scenario '{fileName}'.");

        var input = ToBitmapInput(snapshot);
        return (snapshot, input);
    }

    public static BitmapInput ToBitmapInput(ScenarioSnapshot snapshot)
    {
        var agents = snapshot.Agents
            .Select(a => new BitmapAgent(
                Id: a.Id,
                DisplayName: a.DisplayName,
                TargetHours: a.TargetHours,
                PreferredShiftSymbols: new HashSet<CellSymbol>(),
                MaxWeeklyHours: snapshot.MaxWeeklyHours,
                MaxConsecutiveDays: snapshot.MaxConsecutiveDays,
                MinPauseHours: 11m))
            .ToList();

        var assignments = snapshot.Assignments
            .Select(a =>
            {
                var date = DateOnly.Parse(a.Date);
                var start = TimeOnly.Parse(a.StartTime);
                var end = TimeOnly.Parse(a.EndTime);
                var symbol = SymbolFromStartTime(start);
                var startAt = date.ToDateTime(start);
                var endAt = end < start ? date.AddDays(1).ToDateTime(end) : date.ToDateTime(end);
                return new BitmapAssignment(
                    AgentId: a.AgentId,
                    Date: date,
                    Symbol: symbol,
                    ShiftRefId: Guid.Parse(a.ShiftId),
                    WorkIds: new[] { Guid.NewGuid() },
                    IsLocked: false,
                    StartAt: startAt,
                    EndAt: endAt,
                    Hours: a.Hours);
            })
            .ToList();

        return new BitmapInput(
            Agents: agents,
            StartDate: DateOnly.Parse(snapshot.FromDate),
            EndDate: DateOnly.Parse(snapshot.UntilDate),
            Assignments: assignments);
    }

    private static CellSymbol SymbolFromStartTime(TimeOnly start)
    {
        var typeIndex = ShiftTypeInference.FromStartTime(start);
        return typeIndex switch
        {
            0 => CellSymbol.Early,
            1 => CellSymbol.Late,
            2 => CellSymbol.Night,
            _ => CellSymbol.Other,
        };
    }

    private static string ResolveScenarioPath(string fileName)
    {
        var baseDir = AppContext.BaseDirectory;
        var current = new DirectoryInfo(baseDir);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, ScenariosFolder, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }
        throw new FileNotFoundException(
            $"Scenario '{fileName}' not found by walking up from '{baseDir}'.");
    }
}
