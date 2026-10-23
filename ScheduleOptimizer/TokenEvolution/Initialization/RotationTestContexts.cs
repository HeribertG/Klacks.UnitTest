// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

/// <summary>
/// Scenario 4 as a Wizard 1 context: three orders around the clock (early 07-15, late 15-23, night 23-07, one seat each,
/// so 3/3/3 shifts per day), 15 shift workers over four weeks starting on a Monday, 12 with 136 h and 3 with 128 h
/// guaranteed (together exactly the 252 shifts), soft blocks of 5 days and 2 rest days. Optionally one agent has two
/// weeks of vacation.
/// </summary>
internal static class RotationTestContexts
{
    public static readonly DateOnly From = new(2026, 11, 2);
    public const int Days = 28;
    public const int Agents = 15;

    public static CoreWizardContext Scenario4(bool vacation = false)
    {
        var agents = Enumerable.Range(0, Agents)
            .Select(i => new CoreAgent($"MA{i + 1:00}", 0, i < 12 ? 136 : 128, 6, 11, 0.5, 10, 50, 2)
            {
                FullTime = 168,
                MaxWorkDays = 5,
                MinRestDays = 2,
                PerformsShiftWork = true,
                WorkOnSaturday = true,
                WorkOnSunday = true,
                MaximumHours = 180,
            })
            .ToList();
        var shifts = new List<CoreShift>();
        for (var d = 0; d < Days; d++)
        {
            var date = From.AddDays(d).ToString("yyyy-MM-dd");
            for (var order = 0; order < 3; order++)
            {
                shifts.Add(new CoreShift(Guid.NewGuid().ToString(), "FD", date, "07:00", "15:00", 8, 1, 0) { LocationContext = $"O{order}" });
                shifts.Add(new CoreShift(Guid.NewGuid().ToString(), "SD", date, "15:00", "23:00", 8, 1, 0) { LocationContext = $"O{order}" });
                shifts.Add(new CoreShift(Guid.NewGuid().ToString(), "ND", date, "23:00", "07:00", 8, 1, 0) { LocationContext = $"O{order}" });
            }
        }

        return new CoreWizardContext
        {
            PeriodFrom = From,
            PeriodUntil = From.AddDays(Days - 1),
            Agents = agents,
            Shifts = shifts,
            BreakBlockers = vacation ? [new CoreBreakBlocker("MA03", From.AddDays(7), From.AddDays(20), "Vacation")] : [],
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMinPauseHours = 11,
            SchedulingMaxDailyHours = 10,
            SchedulingMaxWeeklyHours = 50,
        };
    }

    /// <summary>Scenario 4 demand with only four agents: the canon on its own leaves most seats open.</summary>
    public static CoreWizardContext SmallTeam()
    {
        var context = Scenario4();
        return new CoreWizardContext
        {
            PeriodFrom = context.PeriodFrom,
            PeriodUntil = context.PeriodUntil,
            Agents = context.Agents.Take(4).ToList(),
            Shifts = context.Shifts,
            SchedulingMaxConsecutiveDays = context.SchedulingMaxConsecutiveDays,
            SchedulingMinPauseHours = context.SchedulingMinPauseHours,
            SchedulingMaxDailyHours = context.SchedulingMaxDailyHours,
            SchedulingMaxWeeklyHours = context.SchedulingMaxWeeklyHours,
        };
    }
}
