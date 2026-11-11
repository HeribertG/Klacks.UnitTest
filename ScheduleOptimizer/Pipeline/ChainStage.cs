// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Global;
using Klacks.ScheduleOptimizer.Harmonizer.Rules;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;

namespace Klacks.UnitTest.ScheduleOptimizer.Pipeline;

/// <summary>
/// In-memory stage for the whole autofill chain, wired like production without a database: Wizard 1 is the
/// TokenEvolutionLoop with a fixed seed and one evaluation thread, Wizard 2 the conductor-only run of HarmonizerJobRunner
/// (population 2, no generations, harmony guard on, regression gate), Wizard 3 the deterministic engine
/// (HolisticHarmonizerDeterministicEngine: DeterministicSearchOptions.Harmonized). Each later stage starts from a
/// BitmapInput built from the previous stage's plan, as the context builder rebuilds it from the scenario of the stage
/// before; the planner's row order travels with KeepAgentOrder like an AgentOrderIsUserDefined run.
/// The stage: two full ISO weeks (Monday 2026-01-05 to Sunday 2026-01-18), an early duty 06-14 and a late duty 14-22 of
/// 8 h with one seat each per day (28 places), five people in the planner order D, A, C, B, E. E may work 24 h a week but
/// is guaranteed 80 h; B may work 7 h a day, so no 8-hour duty fits B on any day. The same caps reach Wizard 1
/// (CoreAgent, contract day) and Wizards 2 and 3 (BitmapAgent, day availability). Rest days are not part of the stage
/// (zero on both sides), because CoreAgent.MinRestDays and BitmapAgent.MinRestDays do not mean the same rule.
/// </summary>
internal static class ChainStage
{
    public const int DayCount = 14;
    public const int ShiftsPerDay = 2;
    public const int Places = DayCount * ShiftsPerDay;
    public const int MaxConsecutiveDays = 6;
    public const double MinRestHours = 11;
    public const char EarlyMark = 'F';
    public const char LateMark = 'S';
    public const char FreeMark = '.';
    public const char SeveralMark = '+';

    private const double ShiftHours = 8;
    private const int SeatsPerShift = 1;
    private const int ShiftPriority = 0;
    private const double DefaultDailyHours = 10;
    private const double LowDailyHours = 7;
    private const double DefaultWeeklyHours = 50;
    private const double LowWeeklyHours = 24;
    private const double GuaranteedD = 40;
    private const double GuaranteedA = 64;
    private const double GuaranteedC = 48;
    private const double GuaranteedB = 56;
    private const double GuaranteedE = 80;
    private const double MaxOptimalGap = 2;
    private const double Motivation = 0.5;
    private const int MaxWorkDays = 5;
    private const int Wizard1Seed = 11;
    private const int HarmonizerSeed = 7;
    private const double EmergencyThreshold = 0.5;
    private const int ConductorOnlyPopulationSize = 2;
    private const int ConductorOnlyMaxGenerations = 0;
    private const string IsoDate = "yyyy-MM-dd";
    private const string EarlyStart = "06:00";
    private const string EarlyEnd = "14:00";
    private const string LateStart = "14:00";
    private const string LateEnd = "22:00";
    private const short EarlyWorkKind = 1;
    private const short LateWorkKind = 2;
    private const int GuidTailLength = 8;
    private const string EarlyName = "Early";
    private const string LateName = "Late";

    public static readonly DateOnly From = new(2026, 1, 5);
    public static readonly DateOnly Until = From.AddDays(DayCount - 1);

    public static readonly Guid EarlyShiftId = new("00000000-0000-0000-0000-0000000c0001");
    public static readonly Guid LateShiftId = new("00000000-0000-0000-0000-0000000c0002");

    /// <summary>The planner's hand-arranged row order; the automatic order (target hours descending) would be E, A, B, C, D.</summary>
    public static readonly IReadOnlyList<Person> PlannerOrder =
    [
        new("D", GuaranteedD, DefaultDailyHours, DefaultWeeklyHours),
        new("A", GuaranteedA, DefaultDailyHours, DefaultWeeklyHours),
        new("C", GuaranteedC, DefaultDailyHours, DefaultWeeklyHours),
        new("B", GuaranteedB, LowDailyHours, DefaultWeeklyHours),
        new("E", GuaranteedE, DefaultDailyHours, LowWeeklyHours),
    ];

    /// <summary>One person of the stage and the caps every wizard receives for them.</summary>
    /// <param name="Id">Agent id, also the row label of the plan matrix</param>
    /// <param name="GuaranteedHours">Guaranteed hours over the two weeks (Wizard 1 target, Wizard 2/3 target hours)</param>
    /// <param name="MaxDailyHours">Maximum paid hours per day</param>
    /// <param name="MaxWeeklyHours">Maximum paid hours per ISO calendar week</param>
    public sealed record Person(string Id, double GuaranteedHours, double MaxDailyHours, double MaxWeeklyHours);

    /// <summary>One work of a stage's plan, independent of the stage's representation (token or bitmap cell).</summary>
    public sealed record PlanEntry(string AgentId, DateOnly Date, Guid ShiftRefId, DateTime StartAt, DateTime EndAt, decimal Hours);

    /// <summary>The plans of one chain run plus what the run needs to prove it was not cut by a wall clock.</summary>
    /// <param name="Wizard1">Plan after Wizard 1</param>
    /// <param name="Wizard2">Bitmap after Wizard 2 (rows in the order the stage kept)</param>
    /// <param name="Wizard3">Bitmap after Wizard 3</param>
    /// <param name="AutomaticRowOrder">Row order the row sorter would produce without KeepAgentOrder</param>
    /// <param name="Wizard3Search">Search result of the deterministic stage 3</param>
    public sealed record ChainRun(
        IReadOnlyList<PlanEntry> Wizard1,
        HarmonyBitmap Wizard2,
        HarmonyBitmap Wizard3,
        IReadOnlyList<string> AutomaticRowOrder,
        DeterministicSearchResult Wizard3Search);

    public static IEnumerable<DateOnly> Days()
    {
        for (var day = From; day <= Until; day = day.AddDays(1))
        {
            yield return day;
        }
    }

    public static Person PersonOf(string agentId) => PlannerOrder.Single(p => p.Id == agentId);

    public static ChainRun RunChain()
    {
        var context = Wizard1Context();
        var plan = TokenEvolutionLoop.Create().Run(context, new TokenEvolutionConfig
        {
            RandomSeed = Wizard1Seed,
            EvaluationParallelism = 1,
        });
        var wizard1 = plan.Tokens
            .Select(t => new PlanEntry(t.AgentId, t.Date, t.ShiftRefId, t.StartAt, t.EndAt, t.TotalHours))
            .OrderBy(e => e.AgentId, StringComparer.Ordinal).ThenBy(e => e.StartAt)
            .ToList();

        var harmonizerInput = BitmapInputOf(wizard1.Select(ToAssignment).ToList());
        var automaticOrder = RowSorter.Sort(BitmapBuilder.Build(harmonizerInput), keepIncomingOrder: false).Rows.Select(r => r.Id).ToList();
        var harmonized = RunHarmonizer(harmonizerInput);

        var polishInput = BitmapInputOf(AssignmentsOf(harmonized));
        var polished = RowSorter.Sort(BitmapBuilder.Build(polishInput), polishInput.KeepAgentOrder);
        var fitness = new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer());
        var components = HolisticHarmonizerComponents.Build(polishInput, fitness, HolisticHarmonizerComponents.UntrimmedPool);
        var search = new DeterministicHarmonyOptimizer(components, DeterministicSearchOptions.Harmonized)
            .Run(polished, null, CancellationToken.None);

        return new ChainRun(wizard1, harmonized, polished, automaticOrder, search);
    }

    /// <summary>The works a bitmap holds: every cell that is neither free nor an absence.</summary>
    public static IReadOnlyList<PlanEntry> EntriesOf(HarmonyBitmap bitmap)
    {
        var entries = new List<PlanEntry>();
        for (var row = 0; row < bitmap.RowCount; row++)
        {
            for (var day = 0; day < bitmap.DayCount; day++)
            {
                var cell = bitmap.GetCell(row, day);
                if (cell.Symbol is CellSymbol.Free or CellSymbol.Break)
                {
                    continue;
                }

                entries.Add(new PlanEntry(
                    bitmap.Rows[row].Id, bitmap.Days[day], cell.ShiftRefId ?? Guid.Empty, cell.StartAt, cell.EndAt, cell.Hours));
            }
        }

        return entries;
    }

    private static CoreWizardContext Wizard1Context()
    {
        var shifts = new List<CoreShift>();
        foreach (var day in Days())
        {
            var date = day.ToString(IsoDate);
            shifts.Add(new CoreShift(EarlyShiftId.ToString(), EarlyName, date, EarlyStart, EarlyEnd, ShiftHours, SeatsPerShift, ShiftPriority));
            shifts.Add(new CoreShift(LateShiftId.ToString(), LateName, date, LateStart, LateEnd, ShiftHours, SeatsPerShift, ShiftPriority));
        }

        return new CoreWizardContext
        {
            PeriodFrom = From,
            PeriodUntil = Until,
            Agents = PlannerOrder.Select(CoreAgentOf).ToList(),
            Shifts = shifts,
            ContractDays = PlannerOrder
                .SelectMany(p => Days().Select(day => new CoreContractDay(
                    AgentId: p.Id,
                    Date: day,
                    WorksOnDay: true,
                    PerformsShiftWork: true,
                    FullTimeShare: 1,
                    MaximumHoursPerDay: p.MaxDailyHours,
                    ContractId: Guid.Empty)))
                .ToList(),
            SchedulingMaxConsecutiveDays = MaxConsecutiveDays,
            SchedulingMinPauseHours = MinRestHours,
            SchedulingMaxOptimalGap = MaxOptimalGap,
            SchedulingMaxDailyHours = DefaultDailyHours,
            SchedulingMaxWeeklyHours = DefaultWeeklyHours,
        };
    }

    private static CoreAgent CoreAgentOf(Person person) => new(
        Id: person.Id,
        CurrentHours: 0,
        GuaranteedHours: person.GuaranteedHours,
        MaxConsecutiveDays: MaxConsecutiveDays,
        MinRestHours: MinRestHours,
        Motivation: Motivation,
        MaxDailyHours: person.MaxDailyHours,
        MaxWeeklyHours: person.MaxWeeklyHours,
        MaxOptimalGap: MaxOptimalGap)
    {
        FullTime = person.GuaranteedHours,
        MaxWorkDays = MaxWorkDays,
        PerformsShiftWork = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
    };

    /// <summary>Wizard 2/3 input as the harmonizer context builder materialises it for the same people and caps.</summary>
    private static BitmapInput BitmapInputOf(IReadOnlyList<BitmapAssignment> assignments)
    {
        var agents = PlannerOrder
            .Select(p => new BitmapAgent(
                Id: p.Id,
                DisplayName: p.Id,
                TargetHours: (decimal)p.GuaranteedHours,
                PreferredShiftSymbols: new HashSet<CellSymbol>(),
                MaxWeeklyHours: (decimal)p.MaxWeeklyHours,
                MaxConsecutiveDays: MaxConsecutiveDays,
                MinPauseHours: (decimal)MinRestHours,
                MaxDailyHours: (decimal)p.MaxDailyHours))
            .ToList();

        var availability = new Dictionary<(string AgentId, DateOnly Date), DayAvailability>();
        foreach (var person in PlannerOrder)
        {
            foreach (var day in Days())
            {
                availability[(person.Id, day)] = new DayAvailability(
                    WorksOnDay: true, HasFreeCommand: false, HasBreakBlocker: false, MaxDailyHours: (decimal)person.MaxDailyHours);
            }
        }

        return new BitmapInput(agents, From, Until, assignments, Availability: availability, KeepAgentOrder: true);
    }

    internal static HarmonyBitmap RunHarmonizer(BitmapInput input)
    {
        var start = RowSorter.Sort(BitmapBuilder.Build(input), input.KeepAgentOrder);
        var original = BitmapCloner.Clone(start);
        var scorer = new HarmonyScorer();
        IReplaceValidator validator = DomainAwareReplaceValidator.ForInput(input, BitmapRuleRuntime.TryCreate(input));
        validator = HarmonyGuardReplaceValidator.ForStart(validator, start);
        var fitness = new HarmonyFitnessEvaluator(scorer);
        var config = new HarmonizerEvolutionConfig(
            PopulationSize: ConductorOnlyPopulationSize, MaxGenerations: ConductorOnlyMaxGenerations, Seed: HarmonizerSeed);

        HarmonizerConductor BuildConductor(int rowCount) => new(
            scorer,
            new ReplaceMutation(scorer, validator),
            new EmergencyUnlockManager(new EmergencyUnlockState(rowCount), EmergencyThreshold),
            hints: input.SofteningHints,
            blockSwapMutation: new BlockSwapMutation(scorer, validator));

        var loop = new HarmonizerEvolutionLoop(fitness, new StochasticBitmapMutation(validator), BuildConductor, config);
        var initialFitness = fitness.Evaluate(start).Fitness;
        var best = loop.Run(start).Best;
        return best.Fitness < initialFitness ? original : best.Bitmap;
    }

    private static BitmapAssignment ToAssignment(PlanEntry entry) => new(
        entry.AgentId,
        entry.Date,
        SymbolOf(entry.StartAt, entry.EndAt),
        entry.ShiftRefId,
        [WorkIdOf(entry)],
        false,
        entry.StartAt,
        entry.EndAt,
        entry.Hours);

    private static IReadOnlyList<BitmapAssignment> AssignmentsOf(HarmonyBitmap bitmap)
    {
        var assignments = new List<BitmapAssignment>();
        for (var row = 0; row < bitmap.RowCount; row++)
        {
            for (var day = 0; day < bitmap.DayCount; day++)
            {
                var cell = bitmap.GetCell(row, day);
                if (cell.Symbol == CellSymbol.Free)
                {
                    continue;
                }

                assignments.Add(new BitmapAssignment(
                    bitmap.Rows[row].Id, bitmap.Days[day], cell.Symbol, cell.ShiftRefId ?? Guid.Empty, cell.WorkIds,
                    cell.IsLocked, cell.StartAt, cell.EndAt, cell.Hours));
            }
        }

        return assignments;
    }

    /// <summary>Same symbol rule as the context builder (HarmonizerContextBuilder.SymbolOfSpan).</summary>
    internal static CellSymbol SymbolOf(DateTime startAt, DateTime endAt)
        => ShiftTypeInference.FromSpan(TimeOnly.FromDateTime(startAt), TimeOnly.FromDateTime(endAt)) switch
        {
            ShiftTypeInference.EarlyIndex => CellSymbol.Early,
            ShiftTypeInference.LateIndex => CellSymbol.Late,
            ShiftTypeInference.NightIndex => CellSymbol.Night,
            _ => CellSymbol.Other,
        };

    /// <summary>Stable work id per (shift, day): one seat per shift and day, so the pair is unique and runs stay comparable.</summary>
    private static Guid WorkIdOf(PlanEntry entry)
        => new(entry.Date.DayNumber, entry.ShiftRefId == EarlyShiftId ? EarlyWorkKind : LateWorkKind, 0, new byte[GuidTailLength]);
}
