// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.ScheduleOptimizer.StandIns;

/// <summary>
/// In-memory stage of clip S1 (a big event under the key "Messe"): four fair days with the services "Messe Einlass"
/// (08-16) and "Messe Abend" (15-23), ten seats each per day, so 80 places. Real people can fill exactly 65 of them:
/// 14 temporary staff without guaranteed hours on all four days (one service a day: the services overlap and an evening
/// followed by an entry breaks the 11-hour rest) and 6 regular employees who are free on one or two fair days only.
/// The 8 stand-ins (placeholders) have no target hours, but the full-time hours of the settings defaults, as the contract
/// provider gives them.
/// </summary>
/// <param name="regularCurrentHours">Hours the regular employees already hold before the fair days.</param>
internal static class FairStage
{
    public const int SeatsPerService = 10;
    public const int TemporaryStaffCount = 14;
    public const int StandInCount = 8;
    public const int RealCapacity = 65;
    public const int Places = 80;
    public const int StandInsOnlySeatsPerService = 4;
    public const int StandInsOnlyPlaces = 32;

    private const double ServiceHours = 8;
    private const double RegularGuaranteedHours = 160;
    private const double RegularFullTime = 180;
    private const double StandInFullTime = 180;
    private const double MaxWeeklyHours = 50;
    private const double MinRestHours = 11;
    private const double MaxDailyHours = 10;
    private const int MaxConsecutiveDays = 6;
    private const double MaxOptimalGap = 2;
    private const double Motivation = 0.5;

    public static readonly DateOnly From = new(2026, 12, 10);
    public static readonly DateOnly Until = new(2026, 12, 13);

    public static readonly Guid EntryShiftId = new("00000000-0000-0000-0000-00000000e001");
    public static readonly Guid EveningShiftId = new("00000000-0000-0000-0000-00000000e002");

    private static readonly (string Id, DateOnly[] Days)[] Regulars =
    [
        ("regular-1", [From, From.AddDays(1)]),
        ("regular-2", [From, From.AddDays(1)]),
        ("regular-3", [From, From.AddDays(1)]),
        ("regular-4", [From.AddDays(2)]),
        ("regular-5", [From.AddDays(2)]),
        ("regular-6", [From.AddDays(2)]),
    ];

    public static IEnumerable<DateOnly> Days()
    {
        for (var day = From; day <= Until; day = day.AddDays(1))
        {
            yield return day;
        }
    }

    public static bool IsStandIn(string agentId) => agentId.StartsWith("stand-in-", StringComparison.Ordinal);

    /// <summary>Wizard 1 context in the roster priority order of the context builder (regulars, temporary staff, stand-ins).</summary>
    public static CoreWizardContext Context(bool withStandIns, double regularCurrentHours)
    {
        var agents = new List<CoreAgent>();
        var contractDays = new List<CoreContractDay>();

        foreach (var (id, days) in Regulars)
        {
            agents.Add(Agent(id, regularCurrentHours, RegularGuaranteedHours, RegularFullTime));
            contractDays.AddRange(Days().Select(day => ContractDay(id, day, days.Contains(day))));
        }

        for (var i = 1; i <= TemporaryStaffCount; i++)
        {
            var id = $"temp-{i:D2}";
            agents.Add(Agent(id, 0, 0, 0));
            contractDays.AddRange(Days().Select(day => ContractDay(id, day, true)));
        }

        if (withStandIns)
        {
            for (var i = 1; i <= StandInCount; i++)
            {
                var id = $"stand-in-{i}";
                agents.Add(Agent(id, 0, 0, StandInFullTime) with { IsStandIn = true });
                contractDays.AddRange(Days().Select(day => ContractDay(id, day, true)));
            }
        }

        return Context(agents, contractDays, SeatsPerService);
    }

    /// <summary>
    /// Wizard 1 context of a group without real people: only the 8 stand-ins and the two fair services with 4 seats each,
    /// so the stand-ins can fill all 32 places (one service a day each).
    /// </summary>
    public static CoreWizardContext StandInsOnlyContext()
    {
        var agents = new List<CoreAgent>();
        var contractDays = new List<CoreContractDay>();
        for (var i = 1; i <= StandInCount; i++)
        {
            var id = $"stand-in-{i}";
            agents.Add(Agent(id, 0, 0, StandInFullTime) with { IsStandIn = true });
            contractDays.AddRange(Days().Select(day => ContractDay(id, day, true)));
        }

        return Context(agents, contractDays, StandInsOnlySeatsPerService);
    }

    private static CoreWizardContext Context(List<CoreAgent> agents, List<CoreContractDay> contractDays, int seatsPerService)
    {
        return new CoreWizardContext
        {
            PeriodFrom = From,
            PeriodUntil = Until,
            Agents = agents,
            Shifts = Shifts(seatsPerService),
            ContractDays = contractDays,
            SchedulingMaxConsecutiveDays = MaxConsecutiveDays,
            SchedulingMinPauseHours = MinRestHours,
            SchedulingMaxOptimalGap = MaxOptimalGap,
            SchedulingMaxDailyHours = MaxDailyHours,
            SchedulingMaxWeeklyHours = MaxWeeklyHours,
        };
    }

    /// <summary>Wizard 2/3 input of a Wizard 1 plan, as the harmonizer context builder materialises it.</summary>
    public static BitmapInput BitmapOf(CoreScenario plan, CoreWizardContext context)
    {
        var agents = context.Agents
            .Select(a => new BitmapAgent(
                Id: a.Id,
                DisplayName: a.Id,
                TargetHours: 0m,
                PreferredShiftSymbols: new HashSet<CellSymbol>(),
                MaxWeeklyHours: (decimal)MaxWeeklyHours,
                MaxConsecutiveDays: MaxConsecutiveDays,
                MinPauseHours: (decimal)MinRestHours,
                IsStandIn: a.IsStandIn))
            .ToList();

        var assignments = plan.Tokens
            .Select(t => new BitmapAssignment(
                t.AgentId,
                t.Date,
                t.ShiftTypeIndex == 0 ? CellSymbol.Early : CellSymbol.Late,
                t.ShiftRefId,
                [Guid.NewGuid()],
                false,
                t.StartAt,
                t.EndAt,
                t.TotalHours))
            .ToList();

        var availability = new Dictionary<(string AgentId, DateOnly Date), DayAvailability>();
        foreach (var day in context.ContractDays)
        {
            availability[(day.AgentId, day.Date)] = new DayAvailability(day.WorksOnDay, HasFreeCommand: false, HasBreakBlocker: false);
        }

        return new BitmapInput(agents, From, Until, assignments, Availability: availability);
    }

    public static int RealPlaces(CoreScenario plan) => plan.Tokens.Count(t => !IsStandIn(t.AgentId));

    public static int StandInPlaces(CoreScenario plan) => plan.Tokens.Count(t => IsStandIn(t.AgentId));

    public static int StandInCells(HarmonyBitmap bitmap)
    {
        var count = 0;
        for (var row = 0; row < bitmap.RowCount; row++)
        {
            if (!bitmap.Rows[row].IsStandIn)
            {
                continue;
            }

            for (var day = 0; day < bitmap.DayCount; day++)
            {
                if (bitmap.GetCell(row, day).Symbol is CellSymbol.Early or CellSymbol.Late)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static List<CoreShift> Shifts(int seatsPerService)
    {
        var shifts = new List<CoreShift>();
        foreach (var day in Days())
        {
            var date = day.ToString("yyyy-MM-dd");
            for (var seat = 0; seat < seatsPerService; seat++)
            {
                shifts.Add(new CoreShift(EntryShiftId.ToString(), "Messe Einlass", date, "08:00", "16:00", ServiceHours, 1, 0));
                shifts.Add(new CoreShift(EveningShiftId.ToString(), "Messe Abend", date, "15:00", "23:00", ServiceHours, 1, 0));
            }
        }

        return shifts;
    }

    private static CoreAgent Agent(string id, double currentHours, double guaranteedHours, double fullTime) => new(
        Id: id,
        CurrentHours: currentHours,
        GuaranteedHours: guaranteedHours,
        MaxConsecutiveDays: MaxConsecutiveDays,
        MinRestHours: MinRestHours,
        Motivation: Motivation,
        MaxDailyHours: MaxDailyHours,
        MaxWeeklyHours: MaxWeeklyHours,
        MaxOptimalGap: MaxOptimalGap)
    {
        FullTime = fullTime,
        PerformsShiftWork = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
    };

    private static CoreContractDay ContractDay(string agentId, DateOnly date, bool worksOnDay) => new(
        AgentId: agentId,
        Date: date,
        WorksOnDay: worksOnDay,
        PerformsShiftWork: true,
        FullTimeShare: 1,
        MaximumHoursPerDay: MaxDailyHours,
        ContractId: Guid.Empty);
}
