// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/**
 * Opt-in benchmark for the stage-3 (Holistic Harmonizer) fixes, never part of a normal CI run (Explicit).
 * Arms:
 *   S2_RAW        stage 1 + stage 2 with raw monthly targets  (= the plan origin/main hands to stage 3)
 *   S2_PRORATED   stage 1 + stage 2 with prorated targets     (fix A)
 *   CTRL_RAW      deterministic control (all candidates + pairs, no LLM) on S2_RAW, raw targets
 *   CTRL_PRORATED deterministic control on S2_PRORATED, prorated targets (A + B evaluator)
 *   LLM_*         production engine with a real model on S2_* (env-gated, see Stage3LlmArm)
 *   PROD_DET      shipped DeterministicHarmonyOptimizer (production defaults) on S2_PRORATED
 * Live-size scenarios (16x37, 30x31) run only in the PROD_DET tests; their CTRL arm is pair-capped.
 * All arms are scored by Stage3BenchmarkMetrics on the prorated yardstick.
 * Run: dotnet test --filter "Category=HarmonizerStage3Benchmark" (single test via FullyQualifiedName~...)
 */

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Benchmark;

[TestFixture, Explicit("Stage-3 benchmark - run manually; slow and (LLM arm) paid.")]
[Category("HarmonizerStage3Benchmark")]
public class HolisticHarmonizerStage3BenchmarkTests
{
    private const int FixedSeed = 42;
    private const int Repetitions = 20;
    private const int SweepSeedCount = 20;
    private const int DefaultLlmRuns = 20;
    private const string PairCapVariable = "KLACKS_BENCH_PAIR_CAP";
    private const string OutputVariable = "KLACKS_BENCH_OUT";
    private const string LlmRunsVariable = "KLACKS_BENCH_LLM_RUNS";
    private const string LlmTargetVariable = "KLACKS_BENCH_LLM_TARGET";
    private const string LlmScenarioVariable = "KLACKS_BENCH_LLM_SCENARIO";
    private const string PairCapsVariable = "KLACKS_BENCH_PAIR_CAPS";
    private const string SweepScenarioVariable = "KLACKS_BENCH_SWEEP_SCENARIO";
    private const string ProdRepetitionsVariable = "KLACKS_BENCH_PROD_REPS";
    private const string ProdScenarioVariable = "KLACKS_BENCH_PROD_SCENARIO";
    private const string DefaultPairCaps = "0,10,20,40,80";
    private const int LiveSizeControlPairCap = 100;
    private const int SweepBudgetSeconds = 600;
    private const char ListSeparator = ',';
    private const string StopSeparator = "/";

    private static readonly Stage3Scenario[] Scenarios =
    [
        Stage3BenchmarkPipeline.Week,
        Stage3BenchmarkPipeline.Month,
        Stage3BenchmarkPipeline.WeekCalibration,
        Stage3BenchmarkPipeline.MonthCalibration,
    ];

    private static readonly Stage3Scenario[] LiveSizeScenarios =
    [
        Stage3BenchmarkPipeline.LiveSize,
        Stage3BenchmarkPipeline.Large,
    ];

    [Test]
    public void PairCapSweep_ProductionDeterministic_LiveSize()
    {
        var caps = (Environment.GetEnvironmentVariable(PairCapsVariable) ?? DefaultPairCaps)
            .Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => int.Parse(c, NumberStyles.Integer, CultureInfo.InvariantCulture))
            .ToList();
        var scenarioFilter = Environment.GetEnvironmentVariable(SweepScenarioVariable);
        var rows = new List<ArmRun>();
        foreach (var scenario in LiveSizeScenarios.Where(s => string.IsNullOrEmpty(scenarioFilter) || s.Name == scenarioFilter))
        {
            var stages = RunProratedStages(scenario);
            rows.Add(Measure(scenario, "S2_PRORATED", 0, stages.Stage2Bitmap, TimeSpan.Zero, 0));
            foreach (var cap in caps)
            {
                var options = DeterministicSearchOptions.Default with
                {
                    PairPoolCap = cap,
                    WallClockBudget = TimeSpan.FromSeconds(SweepBudgetSeconds),
                };
                var row = RunProduction(scenario, $"PROD_DET_cap{cap}", 0, stages, options);
                rows.Add(row);
                Log(row.ToCsv());
            }
        }

        Report("PAIR-CAP SWEEP live size, fixed seed 42, budget 600 s (natural stop)", rows);
        Assert.That(rows, Is.Not.Empty);
    }

    [Test]
    public void FixedSeed_ProductionDeterministicArm_RepeatedTwentyTimes()
    {
        var repetitions = int.TryParse(Environment.GetEnvironmentVariable(ProdRepetitionsVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : Repetitions;
        var scenarioFilter = Environment.GetEnvironmentVariable(ProdScenarioVariable);
        var rows = new List<ArmRun>();
        var scenarioNames = (scenarioFilter ?? string.Empty).Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var scenario in Scenarios.Concat(LiveSizeScenarios).Where(s => scenarioNames.Length == 0 || scenarioNames.Contains(s.Name)))
        {
            var stages = RunProratedStages(scenario);
            var liveSize = LiveSizeScenarios.Contains(scenario);
            for (var rep = 0; rep < repetitions; rep++)
            {
                rows.Add(Measure(scenario, "S2_PRORATED", rep, stages.Stage2Bitmap, TimeSpan.Zero, 0));
                if (rep == 0 || !liveSize)
                {
                    rows.Add(liveSize
                        ? RunControl(scenario, $"CTRL_PRORATED_cap{LiveSizeControlPairCap}", rep, stages, LiveSizeControlPairCap)
                        : RunControl(scenario, "CTRL_PRORATED", rep, stages));
                }
                var row = RunProduction(scenario, "PROD_DET", rep, stages, DeterministicSearchOptions.Default);
                rows.Add(row);
                Log(row.ToCsv());
            }

            foreach (var (arm, options) in VariantArms)
            {
                var variant = RunProduction(scenario, arm, 0, stages, options);
                rows.Add(variant);
                Log(variant.ToCsv());
            }
        }

        Report($"PRODUCTION DETERMINISTIC vs control vs stage 2 (fix A), fixed seed 42, N={repetitions} (live-size CTRL N=1)", rows);
        Assert.That(rows, Is.Not.Empty);
    }

    /// <summary>Single-run variants (the search is deterministic, so N=1 suffices); not production defaults.</summary>
    private static readonly (string Arm, DeterministicSearchOptions Options)[] VariantArms =
    [
        ("PROD_DET_POOL_ONLY", DeterministicSearchOptions.Default with { AlternateNeighbourhoods = false }),
        ("PROD_DET_FULL_ONLY", DeterministicSearchOptions.Default with { AlternateNeighbourhoods = false, IncludeAllSameDaySwaps = true }),
        ("PROD_DET_cap40", DeterministicSearchOptions.Default with { PairPoolCap = 40 }),
    ];

    private static Stage3StageOutput RunProratedStages(Stage3Scenario scenario)
    {
        var raw = Stage3BenchmarkPipeline.RunStages(scenario, FixedSeed, Stage3TargetMode.Raw);
        var prorated = WithStage2(raw, scenario, Stage3TargetMode.Prorated, FixedSeed);
        Log($"{scenario.Name}: stage1 {raw.Stage1Runtime.TotalSeconds:F1}s, stage2 prorated {prorated.Stage2Runtime.TotalSeconds:F2}s, work cells {Stage3BenchmarkMetrics.Measure(prorated.Stage2Bitmap, scenario).WorkCells}, {DescribeRowLoad(prorated.Stage2Bitmap, scenario)}");
        return prorated;
    }

    private static string DescribeRowLoad(Klacks.ScheduleOptimizer.Harmonizer.Bitmap.HarmonyBitmap bitmap, Stage3Scenario scenario)
    {
        var target = Stage3BenchmarkPipeline.TargetFor(scenario, Stage3TargetMode.Prorated);
        var worked = Enumerable.Range(0, bitmap.RowCount)
            .Select(r => Enumerable.Range(0, bitmap.DayCount).Sum(d => bitmap.GetCell(r, d).Hours))
            .ToList();
        return string.Create(CultureInfo.InvariantCulture,
            $"target {target:F1}h, rows over/under/equal {worked.Count(w => w > target)}/{worked.Count(w => w < target)}/{worked.Count(w => w == target)}, worked min/max {worked.Min():F0}/{worked.Max():F0}h");
    }

    private static ArmRun RunProduction(Stage3Scenario scenario, string arm, int run, Stage3StageOutput stages, DeterministicSearchOptions options)
    {
        var watch = Stopwatch.StartNew();
        var (finalBitmap, result) = Stage3DeterministicArm.Run(stages.Stage1Input, stages.Stage2Bitmap, options);
        watch.Stop();
        return Measure(scenario, arm, run, finalBitmap, watch.Elapsed, result.AppliedBatches.Count) with
        {
            Evaluations = (int)result.Evaluations,
            SearchFitness = result.FitnessAfter,
            MaxCandidates = result.MaxCandidatesPerIteration,
            Stop = result.StopReason.ToString(),
            Iterations = result.IterationsRun,
        };
    }

    [Test]
    public void FixedSeed_DeterministicArms_RepeatedTwentyTimes()
    {
        var rows = new List<ArmRun>();
        foreach (var scenario in Scenarios)
        {
            var raw = Stage3BenchmarkPipeline.RunStages(scenario, FixedSeed, Stage3TargetMode.Raw);
            var prorated = WithStage2(raw, scenario, Stage3TargetMode.Prorated, FixedSeed);
            Log($"{scenario.Name}: stage1 {raw.Stage1Runtime.TotalSeconds:F1}s, stage2 raw {raw.Stage2Runtime.TotalSeconds:F2}s, prorated {prorated.Stage2Runtime.TotalSeconds:F2}s");

            for (var rep = 0; rep < Repetitions; rep++)
            {
                rows.Add(Measure(scenario, "S2_RAW", rep, raw.Stage2Bitmap, TimeSpan.Zero, 0));
                rows.Add(Measure(scenario, "S2_PRORATED", rep, prorated.Stage2Bitmap, TimeSpan.Zero, 0));
                rows.Add(RunControl(scenario, "CTRL_RAW", rep, raw));
                rows.Add(RunControl(scenario, "CTRL_PRORATED", rep, prorated));
            }
        }

        Report("FIXED SEED 42, N=20 per arm (stages 1/2 run once, stage 3 repeated)", rows);
        Assert.That(rows, Is.Not.Empty);
    }

    [Test]
    public void SeedSweep_DeterministicArms_TwentySeeds()
    {
        var rows = new List<ArmRun>();
        foreach (var scenario in Scenarios)
        {
            for (var seed = 1; seed <= SweepSeedCount; seed++)
            {
                var raw = Stage3BenchmarkPipeline.RunStages(scenario, seed, Stage3TargetMode.Raw);
                var prorated = WithStage2(raw, scenario, Stage3TargetMode.Prorated, seed);
                rows.Add(Measure(scenario, "S2_RAW", seed, raw.Stage2Bitmap, TimeSpan.Zero, 0));
                rows.Add(Measure(scenario, "S2_PRORATED", seed, prorated.Stage2Bitmap, TimeSpan.Zero, 0));
                rows.Add(RunControl(scenario, "CTRL_RAW", seed, raw));
                rows.Add(RunControl(scenario, "CTRL_PRORATED", seed, prorated));
            }
        }

        Report("SEED SWEEP seeds 1..20 (stage-1/2 seed = run seed), paired across arms", rows);
        Assert.That(rows, Is.Not.Empty);
    }

    [Test]
    public async Task LlmArm_FixedSeed()
    {
        var settings = Stage3LlmSettings.FromEnvironment();
        if (settings is null)
        {
            Assert.Inconclusive(
                $"LLM arm pending: set {Stage3LlmSettings.ApiKeyVariable}, {Stage3LlmSettings.BaseUrlVariable} and {Stage3LlmSettings.ApiModelVariable}.");
            return;
        }

        var runs = int.TryParse(Environment.GetEnvironmentVariable(LlmRunsVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : DefaultLlmRuns;
        var mode = Enum.TryParse<Stage3TargetMode>(Environment.GetEnvironmentVariable(LlmTargetVariable), ignoreCase: true, out var m)
            ? m
            : Stage3TargetMode.Prorated;
        var scenarioFilter = Environment.GetEnvironmentVariable(LlmScenarioVariable);
        var arm = mode == Stage3TargetMode.Raw ? "LLM_RAW" : "LLM_PRORATED";

        var rows = new List<ArmRun>();
        foreach (var scenario in Scenarios.Where(s => string.IsNullOrEmpty(scenarioFilter) || s.Name == scenarioFilter))
        {
            var raw = Stage3BenchmarkPipeline.RunStages(scenario, FixedSeed, Stage3TargetMode.Raw);
            var stages = mode == Stage3TargetMode.Raw ? raw : WithStage2(raw, scenario, mode, FixedSeed);
            for (var run = 0; run < runs; run++)
            {
                var watch = Stopwatch.StartNew();
                var result = await Stage3LlmArm.RunAsync(Stage3BenchmarkPipeline.Rebuild(stages.Stage2Bitmap, scenario, mode), settings, CancellationToken.None);
                watch.Stop();
                var row = Measure(scenario, arm, run, result.FinalBitmap, watch.Elapsed, result.AcceptedBatches) with
                {
                    LlmCalls = result.LlmCalls,
                    InputTokens = result.InputTokens,
                    OutputTokens = result.OutputTokens,
                    WouldDegrade = result.WouldDegradeBatches,
                    Rejected = result.RejectedBatches,
                    Error = result.ParsingError,
                };
                rows.Add(row);
                Log(row.ToCsv());
            }
        }

        Report($"LLM ARM {arm} model={settings.ApiModelId}, fixed seed 42, N={runs}", rows);
        Assert.That(rows, Is.Not.Empty);
    }

    private static Stage3StageOutput WithStage2(Stage3StageOutput raw, Stage3Scenario scenario, Stage3TargetMode mode, int seed)
    {
        var input = raw.Stage1Input with
        {
            Agents = Stage3BenchmarkPipeline.BuildAgents(raw.Stage1Input.Agents.Select(a => a.Id), Stage3BenchmarkPipeline.TargetFor(scenario, mode)),
        };
        var watch = Stopwatch.StartNew();
        var stage2 = Stage3BenchmarkPipeline.RunStage2(input, seed);
        watch.Stop();
        return new Stage3StageOutput(input, stage2, raw.Stage1Runtime, watch.Elapsed);
    }

    private static ArmRun RunControl(Stage3Scenario scenario, string arm, int run, Stage3StageOutput stages, int? pairCap = null)
    {
        var cap = pairCap ?? (int.TryParse(Environment.GetEnvironmentVariable(PairCapVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)
            ? c
            : int.MaxValue);
        var watch = Stopwatch.StartNew();
        var result = Stage3ControlArm.Run(stages.Stage1Input, stages.Stage2Bitmap, cap);
        watch.Stop();
        return Measure(scenario, arm, run, result.FinalBitmap, watch.Elapsed, result.AppliedBatches) with
        {
            Evaluations = result.Evaluations,
            SearchFitness = result.FitnessAfter,
            MaxCandidates = result.MaxCandidatesPerIteration,
        };
    }

    private static ArmRun Measure(Stage3Scenario scenario, string arm, int run, Klacks.ScheduleOptimizer.Harmonizer.Bitmap.HarmonyBitmap bitmap, TimeSpan runtime, int applied)
    {
        var metrics = Stage3BenchmarkMetrics.Measure(bitmap, scenario);
        return new ArmRun(scenario.Name, arm, run, metrics, runtime.TotalSeconds, applied);
    }

    private static void Report(string title, List<ArmRun> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== {title} ===");
        sb.AppendLine("scenario | arm | N | targetDev h (mean±sd) | devRMS h | maxRowDev h | hardViol (mean±sd) [consec/pause/weekH/restDays] | correctedFitness (mean±sd) | runtime s (mean±sd) | applied batches | evals | maxCand | llmCalls | tokens in/out | stop | searchFitness (own row order)");
        foreach (var group in rows.GroupBy(r => (r.Scenario, r.Arm)))
        {
            var g = group.ToList();
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{group.Key.Scenario} | {group.Key.Arm} | {g.Count} | {Stat(g.Select(r => (double)r.Metrics.TargetDeviationHours))} | {Stat(g.Select(r => r.Metrics.TargetDeviationRms))} | {Stat(g.Select(r => (double)r.Metrics.MaxRowDeviationHours))} | {Stat(g.Select(r => (double)r.Metrics.HardViolations))} [{g.Average(r => r.Metrics.ConsecutiveViolations):F1}/{g.Average(r => r.Metrics.PauseViolations):F1}/{g.Average(r => r.Metrics.WeeklyHoursViolations):F1}/{g.Average(r => r.Metrics.RestDayViolations):F1}] | {Stat(g.Select(r => r.Metrics.CorrectedFitness), "F4")} | {Stat(g.Select(r => r.RuntimeSeconds))} | {g.Average(r => r.Applied):F1} | {g.Average(r => r.Evaluations):F0} | {g.Max(r => r.MaxCandidates)} | {g.Average(r => r.LlmCalls):F1} | {g.Sum(r => r.InputTokens)}/{g.Sum(r => r.OutputTokens)} | {string.Join(StopSeparator, g.Select(r => r.Stop).Where(x => !string.IsNullOrEmpty(x)).Distinct())} | {Stat(g.Select(r => r.SearchFitness), "F4")}"));
        }
        Log(sb.ToString());

        var outDir = Environment.GetEnvironmentVariable(OutputVariable);
        if (!string.IsNullOrWhiteSpace(outDir))
        {
            Directory.CreateDirectory(outDir);
            var file = Path.Combine(outDir, $"stage3-{DateTime.UtcNow:yyyyMMddHHmmss}-{rows.First().Arm}.csv");
            File.WriteAllLines(file, new[] { ArmRun.CsvHeader }.Concat(rows.Select(r => r.ToCsv())));
            File.AppendAllText(file + ".summary.txt", sb.ToString());
        }
    }

    private static string Stat(IEnumerable<double> values, string format = "F2")
    {
        var list = values.ToList();
        var mean = list.Average();
        var sd = list.Count > 1 ? Math.Sqrt(list.Sum(v => (v - mean) * (v - mean)) / (list.Count - 1)) : 0;
        return mean.ToString(format, CultureInfo.InvariantCulture) + "±" + sd.ToString(format, CultureInfo.InvariantCulture);
    }

    private static void Log(string line) => TestContext.Progress.WriteLine(line);

    private sealed record ArmRun(string Scenario, string Arm, int Run, Stage3Metrics Metrics, double RuntimeSeconds, int Applied)
    {
        public const string CsvHeader = "scenario,arm,run,targetDevHours,devRms,maxRowDev,hardViolations,consec,pause,weeklyHours,restDays,correctedFitness,runtimeS,applied,evaluations,maxCandidates,llmCalls,inputTokens,outputTokens,wouldDegrade,rejected,error,stop,iterations,searchFitness";

        public int Evaluations { get; init; }
        public int MaxCandidates { get; init; }
        public int LlmCalls { get; init; }
        public long InputTokens { get; init; }
        public long OutputTokens { get; init; }
        public int WouldDegrade { get; init; }
        public int Rejected { get; init; }
        public string? Error { get; init; }
        public string? Stop { get; init; }
        public int Iterations { get; init; }
        public double SearchFitness { get; init; }

        public string ToCsv() => string.Create(CultureInfo.InvariantCulture,
            $"{Scenario},{Arm},{Run},{Metrics.TargetDeviationHours:F3},{Metrics.TargetDeviationRms:F3},{Metrics.MaxRowDeviationHours:F3},{Metrics.HardViolations},{Metrics.ConsecutiveViolations},{Metrics.PauseViolations},{Metrics.WeeklyHoursViolations},{Metrics.RestDayViolations},{Metrics.CorrectedFitness:F5},{RuntimeSeconds:F2},{Applied},{Evaluations},{MaxCandidates},{LlmCalls},{InputTokens},{OutputTokens},{WouldDegrade},{Rejected},\"{(Error ?? string.Empty).Replace("\"", "'", StringComparison.Ordinal)}\",{Stop},{Iterations},{SearchFitness:F5}");
    }
}
