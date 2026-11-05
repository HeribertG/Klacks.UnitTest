// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;

namespace Klacks.UnitTest.ScheduleOptimizer.Constraints.Keywords;

/// <summary>
/// Small builders for the keyword-wish tests: a week starting Monday 2026-04-20, agents working every day, early
/// (07-15), late (15-23) and night (23-07) shifts.
/// </summary>
internal static class KeywordWishTestData
{
    public static readonly DateOnly Monday = new(2026, 4, 20);
    public static readonly DateOnly Sunday = new(2026, 4, 26);

    public static readonly (string Start, string End, int Kind) Early = ("07:00", "15:00", ShiftTypeInference.EarlyIndex);
    public static readonly (string Start, string End, int Kind) Late = ("15:00", "23:00", ShiftTypeInference.LateIndex);
    public static readonly (string Start, string End, int Kind) Night = ("23:00", "07:00", ShiftTypeInference.NightIndex);

    public static CoreAgent Agent(string id, double guaranteedHours = 0) => new(
        Id: id,
        CurrentHours: 0,
        GuaranteedHours: guaranteedHours,
        MaxConsecutiveDays: 6,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        FullTime = 40,
        MaxWorkDays = 6,
        MinRestDays = 1,
        PerformsShiftWork = true,
        WorkOnMonday = true,
        WorkOnTuesday = true,
        WorkOnWednesday = true,
        WorkOnThursday = true,
        WorkOnFriday = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
    };

    public static CoreShift Slot(Guid shiftId, DateOnly date, (string Start, string End, int Kind) kind)
        => new(shiftId.ToString(), "S", date.ToString("yyyy-MM-dd"), kind.Start, kind.End, 8, 1, 0);

    public static CoreToken Token(string agentId, Guid shiftId, DateOnly date, (string Start, string End, int Kind) kind)
    {
        var start = date.ToDateTime(TimeOnly.Parse(kind.Start));
        return new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: kind.Kind,
            Date: date,
            TotalHours: 8,
            StartAt: start,
            EndAt: start.AddHours(8),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: null,
            ShiftRefId: shiftId,
            AgentId: agentId);
    }

    public static CoreWizardContext Context(
        IReadOnlyList<CoreAgent> agents,
        IReadOnlyList<CoreShift> shifts,
        IReadOnlyList<CoreScheduleCommand> commands,
        KeywordMode mode) => new()
        {
            PeriodFrom = Monday,
            PeriodUntil = Sunday,
            Agents = agents,
            Shifts = shifts,
            ScheduleCommands = commands,
            KeywordMode = mode,
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMaxDailyHours = 10,
            SchedulingMinPauseHours = 11,
        };
}
