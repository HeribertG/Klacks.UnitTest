// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/**
 * Stages 1 and 2 of the stage-3 benchmark, DB-free. Stage 1 is the real Wizard-1 TokenEvolutionLoop on the
 * autofill specification scenario (5 employees, 3 shifts per day; 180 h = spec scenario 1, 150 h = calibration 1b); stage 2 is the
 * production Harmonizer composition copied from HarmonizerJobRunner (conductor-only config, the shipped
 * default). The bitmap targets come either raw (origin/main behaviour: monthly GuaranteedHours) or prorated
 * to the planned range (fix A).
 */

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Rules;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Benchmark;

public enum Stage3TargetMode
{
    Raw,
    Prorated,
}

/// <param name="Orders">Parallel orders (3 shifts per day each); null keeps the order-less spec triple.</param>
/// <param name="DayShiftPerOrder">Adds the fourth (day) slot per order; requires <paramref name="Orders"/>.</param>
public sealed record Stage3Scenario(
    string Name,
    DateOnly From,
    DateOnly Until,
    decimal MonthlyGuaranteedHours,
    int EmployeeCount = AutofillSpecConstants.EmployeeCount,
    int? Orders = null,
    bool DayShiftPerOrder = false);

public sealed record Stage3StageOutput(
    BitmapInput Stage1Input,
    HarmonyBitmap Stage2Bitmap,
    TimeSpan Stage1Runtime,
    TimeSpan Stage2Runtime);

public static class Stage3BenchmarkPipeline
{
    private const decimal SpecGuaranteedHours = (decimal)AutofillSpecConstants.GuaranteedHours;
    private const decimal CalibrationGuaranteedHours = (decimal)AutofillSpecConstants.CalibrationGuaranteedHours;
    public const decimal MinPauseHours = (decimal)AutofillSpecConstants.MinRestHours;

    private const double EmergencyUnlockThreshold = 0.5;
    private static readonly TimeSpan Stage2Budget = TimeSpan.FromSeconds(120);

    private static readonly DateOnly WeekFrom = new(2026, 3, 2);
    private static readonly DateOnly WeekUntil = new(2026, 3, 8);

    public static readonly Stage3Scenario Week = new("week-5x7-180h", WeekFrom, WeekUntil, SpecGuaranteedHours);
    public static readonly Stage3Scenario Month = new("month-5x31-180h", AutofillSpecConstants.PeriodFrom, AutofillSpecConstants.PeriodUntil, SpecGuaranteedHours);
    public static readonly Stage3Scenario WeekCalibration = new("week-5x7-150h", WeekFrom, WeekUntil, CalibrationGuaranteedHours);
    public static readonly Stage3Scenario MonthCalibration = new("month-5x31-150h", AutofillSpecConstants.PeriodFrom, AutofillSpecConstants.PeriodUntil, CalibrationGuaranteedHours);

    private const int LiveSizeEmployees = 16;
    private const int LargeEmployees = 30;
    private const int MaxOrders = AutofillShiftCatalog.MaxOrderCount;
    private const decimal LiveSizeGuaranteedHours = 160m;
    private static readonly DateOnly LiveSizeFrom = new(2026, 3, 2);
    private static readonly DateOnly LiveSizeUntil = new(2026, 4, 7);

    /// <summary>Live size of the 2026-05 Haiku runs: 16 employees x 37 days, 3 orders x 4 slots = 12 shifts per day.</summary>
    public static readonly Stage3Scenario LiveSize = new("live-16x37-160h", LiveSizeFrom, LiveSizeUntil, LiveSizeGuaranteedHours, LiveSizeEmployees, MaxOrders, DayShiftPerOrder: true);

    /// <summary>30 employees x 31 days with the same 12 shifts per day (the catalog caps orders at 3), so rows are lightly loaded.</summary>
    public static readonly Stage3Scenario Large = new("large-30x31-160h", AutofillSpecConstants.PeriodFrom, AutofillSpecConstants.PeriodUntil, LiveSizeGuaranteedHours, LargeEmployees, MaxOrders, DayShiftPerOrder: true);

    public static Stage3StageOutput RunStages(Stage3Scenario scenario, int seed, Stage3TargetMode mode)
    {
        var builder = new AutofillScenarioBuilder()
            .WithPeriod(scenario.From, scenario.Until)
            .WithEmployees(scenario.EmployeeCount, (double)scenario.MonthlyGuaranteedHours)
            .WithRandomSeed(seed);
        if (scenario.Orders is { } orders)
        {
            builder = builder.WithOrders(orders);
            if (scenario.DayShiftPerOrder)
            {
                builder = builder.WithDayShiftPerOrder();
            }
        }
        var definition = builder.Build();

        var stage1Watch = System.Diagnostics.Stopwatch.StartNew();
        var plan = TokenEvolutionLoop.Create().Run(definition.Context, definition.Config);
        stage1Watch.Stop();

        var input = ToBitmapInput(definition.Context, plan, scenario, mode);

        var stage2Watch = System.Diagnostics.Stopwatch.StartNew();
        var stage2 = RunStage2(input, seed);
        stage2Watch.Stop();

        return new Stage3StageOutput(input, stage2, stage1Watch.Elapsed, stage2Watch.Elapsed);
    }

    /// <summary>Mirrors HarmonizerJobRunner.RunJobAsync: same scorer, validator, conductor and config builder.</summary>
    public static HarmonyBitmap RunStage2(BitmapInput input, int seed)
    {
        var sorted = RowSorter.Sort(BitmapBuilder.Build(input));
        var scorer = new HarmonyScorer();
        var validator = DomainAwareReplaceValidator.ForInput(input, BitmapRuleRuntime.TryCreate(input));
        var fitness = new HarmonyFitnessEvaluator(scorer);
        var stochastic = new StochasticBitmapMutation(validator);
        var config = HarmonizerJobRunner.BuildEvolutionConfig(useEvolution: false, Stage2Budget, seed);

        HarmonizerConductor BuildConductor(int rowCount)
        {
            var emergency = new EmergencyUnlockManager(new EmergencyUnlockState(rowCount), EmergencyUnlockThreshold);
            return new HarmonizerConductor(
                scorer,
                new ReplaceMutation(scorer, validator),
                emergency,
                hints: input.SofteningHints,
                blockSwapMutation: new BlockSwapMutation(scorer, validator));
        }

        var initial = fitness.Evaluate(sorted);
        var result = new HarmonizerEvolutionLoop(fitness, stochastic, BuildConductor, config).Run(sorted, progress: null, CancellationToken.None);
        return result.Best.Fitness < initial.Fitness ? sorted : result.Best.Bitmap;
    }

    public static decimal TargetFor(Stage3Scenario scenario, Stage3TargetMode mode)
    {
        if (mode == Stage3TargetMode.Raw)
        {
            return scenario.MonthlyGuaranteedHours;
        }

        var total = 0m;
        for (var date = scenario.From; date <= scenario.Until; date = date.AddDays(1))
        {
            total += PayPeriodTargetHoursProrator.DailyShare(scenario.MonthlyGuaranteedHours, PaymentInterval.Monthly, date);
        }
        return total;
    }

    public static IReadOnlyList<BitmapAgent> BuildAgents(IEnumerable<string> agentIds, decimal targetHours)
        => agentIds
            .Select(id => new BitmapAgent(
                Id: id,
                DisplayName: id,
                TargetHours: targetHours,
                PreferredShiftSymbols: new HashSet<CellSymbol>(),
                MaxWeeklyHours: (decimal)AutofillSpecConstants.MaxWeeklyHours,
                MaxConsecutiveDays: AutofillSpecConstants.MaxConsecutiveDays,
                MinPauseHours: MinPauseHours,
                MinRestDays: AutofillSpecConstants.MinRestDays))
            .ToList();

    /// <summary>Re-reads a bitmap as assignments, so any arm's result can be rebuilt with other agent targets.</summary>
    public static IReadOnlyList<BitmapAssignment> ExtractAssignments(HarmonyBitmap bitmap)
    {
        var result = new List<BitmapAssignment>();
        for (var r = 0; r < bitmap.RowCount; r++)
        {
            for (var d = 0; d < bitmap.DayCount; d++)
            {
                var cell = bitmap.GetCell(r, d);
                if (cell.Symbol == CellSymbol.Free)
                {
                    continue;
                }
                result.Add(new BitmapAssignment(
                    AgentId: bitmap.Rows[r].Id,
                    Date: bitmap.Days[d],
                    Symbol: cell.Symbol,
                    ShiftRefId: cell.ShiftRefId ?? Guid.Empty,
                    WorkIds: cell.WorkIds,
                    IsLocked: cell.IsLocked,
                    StartAt: cell.StartAt,
                    EndAt: cell.EndAt,
                    Hours: cell.Hours));
            }
        }
        return result;
    }

    public static BitmapInput Rebuild(HarmonyBitmap bitmap, Stage3Scenario scenario, Stage3TargetMode mode)
        => new(
            BuildAgents(bitmap.Rows.Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal), TargetFor(scenario, mode)),
            scenario.From,
            scenario.Until,
            ExtractAssignments(bitmap));

    private static BitmapInput ToBitmapInput(CoreWizardContext context, CoreScenario plan, Stage3Scenario scenario, Stage3TargetMode mode)
    {
        var assignments = plan.Tokens
            .Where(t => t.Date >= scenario.From && t.Date <= scenario.Until)
            .Select(t => new BitmapAssignment(
                AgentId: t.AgentId,
                Date: t.Date,
                Symbol: SymbolOf(t.ShiftTypeIndex),
                ShiftRefId: t.ShiftRefId,
                WorkIds: [Guid.NewGuid()],
                IsLocked: t.IsLocked,
                StartAt: t.StartAt,
                EndAt: t.EndAt,
                Hours: t.TotalHours))
            .ToList();

        return new BitmapInput(
            BuildAgents(context.Agents.Select(a => a.Id), TargetFor(scenario, mode)),
            scenario.From,
            scenario.Until,
            assignments);
    }

    private static CellSymbol SymbolOf(int shiftTypeIndex) => shiftTypeIndex switch
    {
        0 => CellSymbol.Early,
        1 => CellSymbol.Late,
        2 => CellSymbol.Night,
        _ => CellSymbol.Other,
    };
}
