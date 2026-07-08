// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Metrics;

/// <summary>
/// Code fixtures for the wizard regression test suite. Each method returns a deterministic
/// CoreWizardContext that drives the SlotAuctioneer for the corresponding scenario.
/// </summary>
internal static class WizardScenarioFixtures
{
    private const double FullTimeHours = 180;
    private const double SlotHours = 8;

    public static CoreWizardContext BernFiveFullTimeHomogeneous()
    {
        var from = new DateOnly(2026, 5, 1);
        var until = new DateOnly(2026, 5, 31);
        var agents = new[]
        {
            FullTimeAgent("A-Koch"),
            FullTimeAgent("B-Moeller"),
            FullTimeAgent("C-Richard"),
            FullTimeAgent("D-Thomas"),
            FullTimeAgent("E-Zimmer"),
        };
        var shifts = BuildThreeShiftsPerDay(from, until);
        return BaseContext(from, until, agents, shifts);
    }

    public static CoreWizardContext HeterogeneousMix()
    {
        var from = new DateOnly(2026, 5, 1);
        var until = new DateOnly(2026, 5, 31);
        var agents = new[]
        {
            FullTimeAgent("A-FT1"),
            FullTimeAgent("B-FT2"),
            PartTimeAgent("C-PT1", 90),
            PartTimeAgent("D-PT2", 90),
            NightSpecialistAgent("E-NightOnly"),
        };
        var shifts = BuildThreeShiftsPerDay(from, until);
        return BaseContext(from, until, agents, shifts);
    }

    public static CoreWizardContext BoundaryWithPriorWorks()
    {
        var from = new DateOnly(2026, 5, 1);
        var until = new DateOnly(2026, 5, 14);
        var agents = new[]
        {
            FullTimeAgent("A"),
            FullTimeAgent("B"),
            FullTimeAgent("C"),
        };
        var shifts = BuildThreeShiftsPerDay(from, until);
        var boundaryLocked = BuildBoundaryLockedRun(agentId: "A", lastDayBeforePeriod: from, runLengthDays: 3);
        return new CoreWizardContext
        {
            PeriodFrom = from,
            PeriodUntil = until,
            Agents = agents,
            Shifts = shifts,
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMaxDailyHours = 10,
            SchedulingMaxWeeklyHours = 50,
            SchedulingMinPauseHours = 11,
            BoundaryLockedWorks = boundaryLocked,
        };
    }

    private static CoreAgent FullTimeAgent(string id) => new(
        Id: id,
        CurrentHours: 0,
        GuaranteedHours: FullTimeHours,
        MaxConsecutiveDays: 6,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        FullTime = FullTimeHours,
        MaxWorkDays = 5,
        MinRestDays = 2,
        PerformsShiftWork = true,
        WorkOnMonday = true,
        WorkOnTuesday = true,
        WorkOnWednesday = true,
        WorkOnThursday = true,
        WorkOnFriday = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
        NightRate = 0.10m,
        WE1Rate = 0.25m,
        WE2Rate = 0.50m,
    };

    private static CoreAgent PartTimeAgent(string id, double guaranteed) => FullTimeAgent(id) with
    {
        GuaranteedHours = guaranteed,
        FullTime = guaranteed,
    };

    private static CoreAgent NightSpecialistAgent(string id) => FullTimeAgent(id) with
    {
        GuaranteedHours = 120,
        FullTime = 120,
        NightRate = 0.30m,
    };

    private static IReadOnlyList<CoreShift> BuildThreeShiftsPerDay(DateOnly from, DateOnly until)
    {
        var shifts = new List<CoreShift>();
        for (var d = from; d <= until; d = d.AddDays(1))
        {
            var iso = d.ToString("yyyy-MM-dd");
            shifts.Add(new CoreShift($"{iso}-E", "Frueh", iso, "06:00", "14:00", SlotHours, 1, 0));
            shifts.Add(new CoreShift($"{iso}-L", "Spaet", iso, "14:00", "22:00", SlotHours, 1, 0));
            shifts.Add(new CoreShift($"{iso}-N", "Nacht", iso, "22:00", "06:00", SlotHours, 1, 0));
        }
        return shifts;
    }

    private static CoreWizardContext BaseContext(
        DateOnly from,
        DateOnly until,
        IReadOnlyList<CoreAgent> agents,
        IReadOnlyList<CoreShift> shifts) => new()
        {
            PeriodFrom = from,
            PeriodUntil = until,
            Agents = agents,
            Shifts = shifts,
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMaxDailyHours = 10,
            SchedulingMaxWeeklyHours = 50,
            SchedulingMinPauseHours = 11,
        };

    private static IReadOnlyList<CoreLockedWork> BuildBoundaryLockedRun(string agentId, DateOnly lastDayBeforePeriod, int runLengthDays)
    {
        var works = new List<CoreLockedWork>();
        for (var i = 0; i < runLengthDays; i++)
        {
            var date = lastDayBeforePeriod.AddDays(-(i + 1));
            var start = date.ToDateTime(new TimeOnly(6, 0));
            works.Add(new CoreLockedWork(
                WorkId: Guid.NewGuid().ToString(),
                AgentId: agentId,
                Date: date,
                ShiftTypeIndex: 0,
                TotalHours: (decimal)SlotHours,
                StartAt: start,
                EndAt: start.AddHours(SlotHours),
                ShiftRefId: Guid.Empty,
                LocationContext: null)
            {
                Surcharges = 0,
            });
        }
        return works;
    }
}
