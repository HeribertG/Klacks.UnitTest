// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/**
 * JSON DTO for harmonizer benchmark scenarios. Snapshot of a real or synthetic schedule
 * exported once and replayed in-memory by the research benchmark loop.
 */

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Research;

public sealed record ScenarioSnapshot(
    string Name,
    string FromDate,
    string UntilDate,
    int MaxConsecutiveDays,
    decimal MaxWeeklyHours,
    IReadOnlyList<ScenarioAgent> Agents,
    IReadOnlyList<ScenarioAssignment> Assignments);

public sealed record ScenarioAgent(
    string Id,
    string DisplayName,
    decimal TargetHours);

public sealed record ScenarioAssignment(
    string AgentId,
    string Date,
    string ShiftId,
    string StartTime,
    string EndTime,
    decimal Hours);
