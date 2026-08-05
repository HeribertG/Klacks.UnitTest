// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Metrics;

/// <summary>
/// Code fixtures for the wizard regression test suite. Each method returns a deterministic
/// CoreWizardContext that drives the SlotAuctioneer for the corresponding scenario.
/// Shift ids are stable Guids repeated across all days of the period - the production wire
/// format built by WizardShiftBuilder - so the Guid-gated Stage 0 checks (qualification,
/// blacklist, restricted window) are reachable instead of silently disabled.
/// </summary>
internal static class WizardScenarioFixtures
{
    private const double FullTimeHours = 180;
    private const double SlotHours = 8;

    private const int NightWindowStartMinutes = 22 * 60;
    private const int NightWindowEndMinutes = 6 * 60;
    private const int SeasonStartMonth = 1;
    private const int SeasonStartDay = 1;
    private const int SeasonEndMonth = 12;
    private const int SeasonEndDay = 31;

    private const string BlacklistedAgentId = "E-NightOnly";
    private const string UnqualifiedAgentId = "C-PT1";

    public static readonly Guid EarlyShiftId = new("00000000-0000-0000-0000-000000000301");
    public static readonly Guid LateShiftId = new("00000000-0000-0000-0000-000000000302");
    public static readonly Guid NightShiftId = new("00000000-0000-0000-0000-000000000303");

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
        var inputs = HeterogeneousMixInputs();
        return BaseContext(inputs.From, inputs.Until, inputs.Agents, inputs.Shifts);
    }

    /// <summary>
    /// Same agents and shifts as <see cref="HeterogeneousMix"/>, but carrying eligibility data:
    /// <see cref="BlacklistedAgentId"/> is blacklisted for the early shift and
    /// <see cref="UnqualifiedAgentId"/> lacks the qualification for the night shift on every day
    /// of the period. Used by the guard tests that prove the Stage 0 checks really fire.
    /// </summary>
    public static CoreWizardContext HeterogeneousMixWithEligibilityData()
    {
        var inputs = HeterogeneousMixInputs();
        var preferences = new[]
        {
            new CoreShiftPreference(BlacklistedAgentId, EarlyShiftId, ShiftPreferenceKind.Blacklist),
        };
        var ineligible = new HashSet<(string, Guid, DateOnly)>();
        for (var d = inputs.From; d <= inputs.Until; d = d.AddDays(1))
        {
            ineligible.Add((UnqualifiedAgentId, NightShiftId, d));
        }

        return BaseContext(
            inputs.From,
            inputs.Until,
            inputs.Agents,
            inputs.Shifts,
            shiftPreferences: preferences,
            ineligibleAssignments: ineligible);
    }

    /// <summary>
    /// Same agents and shifts as <see cref="HeterogeneousMix"/> plus a year-round K16 window that
    /// blocks the night shift between 22:00 and 06:00.
    /// </summary>
    public static CoreWizardContext HeterogeneousMixWithRestrictedNightWindow()
    {
        var inputs = HeterogeneousMixInputs();
        var window = new CoreRestrictedTimeWindow(
            FromMonth: SeasonStartMonth,
            FromDay: SeasonStartDay,
            ToMonth: SeasonEndMonth,
            ToDay: SeasonEndDay,
            DailyStartMinutes: NightWindowStartMinutes,
            DailyEndMinutes: NightWindowEndMinutes,
            RestrictedShiftIds: new HashSet<Guid> { NightShiftId });

        return BaseContext(
            inputs.From,
            inputs.Until,
            inputs.Agents,
            inputs.Shifts,
            restrictedTimeWindows: [window]);
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

    private static (DateOnly From, DateOnly Until, IReadOnlyList<CoreAgent> Agents, IReadOnlyList<CoreShift> Shifts)
        HeterogeneousMixInputs()
    {
        var from = new DateOnly(2026, 5, 1);
        var until = new DateOnly(2026, 5, 31);
        var agents = new[]
        {
            FullTimeAgent("A-FT1"),
            FullTimeAgent("B-FT2"),
            PartTimeAgent(UnqualifiedAgentId, 90),
            PartTimeAgent("D-PT2", 90),
            NightSpecialistAgent(BlacklistedAgentId),
        };
        return (from, until, agents, BuildThreeShiftsPerDay(from, until));
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
            shifts.Add(new CoreShift(EarlyShiftId.ToString(), "Frueh", iso, "06:00", "14:00", SlotHours, 1, 0));
            shifts.Add(new CoreShift(LateShiftId.ToString(), "Spaet", iso, "14:00", "22:00", SlotHours, 1, 0));
            shifts.Add(new CoreShift(NightShiftId.ToString(), "Nacht", iso, "22:00", "06:00", SlotHours, 1, 0));
        }
        return shifts;
    }

    private static CoreWizardContext BaseContext(
        DateOnly from,
        DateOnly until,
        IReadOnlyList<CoreAgent> agents,
        IReadOnlyList<CoreShift> shifts,
        IReadOnlyList<CoreShiftPreference>? shiftPreferences = null,
        IReadOnlySet<(string AgentId, Guid ShiftId, DateOnly Date)>? ineligibleAssignments = null,
        IReadOnlyList<CoreRestrictedTimeWindow>? restrictedTimeWindows = null) => new()
        {
            PeriodFrom = from,
            PeriodUntil = until,
            Agents = agents,
            Shifts = shifts,
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMaxDailyHours = 10,
            SchedulingMaxWeeklyHours = 50,
            SchedulingMinPauseHours = 11,
            ShiftPreferences = shiftPreferences ?? [],
            IneligibleAssignments = ineligibleAssignments ?? new HashSet<(string, Guid, DateOnly)>(),
            RestrictedTimeWindows = restrictedTimeWindows ?? [],
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
                ShiftRefId: EarlyShiftId,
                LocationContext: null)
            {
                Surcharges = 0,
            });
        }
        return works;
    }
}
