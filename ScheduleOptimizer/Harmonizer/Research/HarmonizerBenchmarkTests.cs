// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/**
 * Phase 1 of the Harmonizer Research loop. Loads a frozen snapshot OR generates a synthetic
 * scenario, runs the production HarmonyScorer + Conductor + Evolution loop in-memory, and
 * reports per-scenario quality metrics plus an aggregated AUTORESEARCH_SCORE that the
 * autoresearch shell loop reads.
 *
 * Two modes per scenario:
 *   - Conductor-only (single greedy pass) to isolate scorer/mutation effects
 *   - Full evolution loop (population + mutations) to measure the actual production path
 */

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Research;

[TestFixture]
[Category("HarmonizerResearch")]
public class HarmonizerBenchmarkTests
{
    private const int Seed = 42;

    [Test]
    public void Aggregate_AllScenarios_ReportTotalScore()
    {
        var scenarios = LoadAllScenarios();
        var perScenario = new List<(string Name, ResearchScoreBreakdown Score, RunResult Run)>();
        double aggregate = 0;
        double planOnlyAggregate = 0;

        foreach (var (snapshot, input) in scenarios)
        {
            var conductorOnly = RunConductorOnly(input);
            var evolution = RunEvolution(input);

            TestContext.Progress.WriteLine($"=== Scenario: {snapshot.Name} ===");
            ReportRun("CONDUCTOR_ONLY", conductorOnly);
            ReportRun("EVOLUTION", evolution);
            PrintPerAgentDelta(evolution);
            PrintPerRowLies(snapshot.Name, evolution);

            perScenario.Add((snapshot.Name, evolution.Score, evolution));
            aggregate += evolution.Score.Total;
            if (snapshot.Name.StartsWith("plan-real", StringComparison.Ordinal))
            {
                planOnlyAggregate += evolution.Score.Total;
            }
        }

        TestContext.Progress.WriteLine("=== AGGREGATE ===");
        TestContext.Progress.WriteLine($"AUTORESEARCH_SCORE: {aggregate:F4}");
        TestContext.Progress.WriteLine($"AUTORESEARCH_SCORE_PLAN_ONLY: {planOnlyAggregate:F4}");
        TestContext.Progress.WriteLine($"AUTORESEARCH_SCENARIO_COUNT: {scenarios.Count}");
        TestContext.Progress.WriteLine(
            $"AUTORESEARCH_SCENARIOS_WITH_LIE: {perScenario.Count(s => s.Score.ScorerLies)}");
        TestContext.Progress.WriteLine(
            $"AUTORESEARCH_TOTAL_VIOLATIONS: {perScenario.Sum(s => s.Score.ViolationsAfter)}");

        Assert.That(scenarios, Is.Not.Empty);
    }

    private static void PrintPerRowLies(string scenarioName, RunResult run)
    {
        var lies = new List<string>();
        for (var i = 0; i < run.Before.Rows.Count; i++)
        {
            var b = run.Before.Rows[i];
            var a = run.After.Rows[i];
            var qualityDown = a.Blocks > b.Blocks
                || a.AvgBlockLength < b.AvgBlockLength
                || a.Violations > b.Violations;
            if (qualityDown)
            {
                lies.Add($"{a.DisplayName} (blocks {b.Blocks}->{a.Blocks}, len {b.AvgBlockLength:F2}->{a.AvgBlockLength:F2}, vio {b.Violations}->{a.Violations})");
            }
        }
        if (lies.Count > 0)
        {
            TestContext.Progress.WriteLine($"AUTORESEARCH_LIE_ROWS {scenarioName}: {lies.Count}");
            foreach (var l in lies)
            {
                TestContext.Progress.WriteLine($"  ! {l}");
            }
        }
    }

    private static IReadOnlyList<(ScenarioSnapshot Snapshot, BitmapInput Input)> LoadAllScenarios()
    {
        var list = new List<(ScenarioSnapshot, BitmapInput)>
        {
            ScenarioLoader.Load("plan-real-5x37.json"),
            SyntheticScenarioFactory.Tiny(),
            SyntheticScenarioFactory.Mid(),
            SyntheticScenarioFactory.EdgeCases(),
        };
        return list;
    }

    private sealed record RunResult(
        ScheduleQualityReport Before,
        ScheduleQualityReport After,
        double HarmonyBefore,
        double HarmonyAfter,
        ResearchScoreBreakdown Score);

    private static RunResult RunConductorOnly(BitmapInput input)
    {
        var bitmap = BitmapBuilder.Build(input);
        var bitmapBefore = CloneBitmap(bitmap);

        var scorer = new HarmonyScorer();
        var fitness = new HarmonyFitnessEvaluator(scorer);
        var harmonyBefore = fitness.Evaluate(bitmapBefore).Fitness;
        var qualityBefore = ScheduleQualityMetrics.Compute(bitmapBefore);

        var validator = new DomainAwareReplaceValidator(availability: null);
        var mutation = new ReplaceMutation(scorer, validator);
        var blockSwap = new BlockSwapMutation(scorer, validator);
        var emergency = new EmergencyUnlockManager(new EmergencyUnlockState(bitmap.RowCount));
        var conductor = new HarmonizerConductor(scorer, mutation, emergency, blockSwapMutation: blockSwap);
        conductor.Run(bitmap);

        var harmonyAfter = fitness.Evaluate(bitmap).Fitness;
        var qualityAfter = ScheduleQualityMetrics.Compute(bitmap);
        var score = ResearchScore.Compute(qualityBefore, qualityAfter, harmonyBefore, harmonyAfter);
        return new RunResult(qualityBefore, qualityAfter, harmonyBefore, harmonyAfter, score);
    }

    private static RunResult RunEvolution(BitmapInput input)
    {
        var bitmap = BitmapBuilder.Build(input);
        var bitmapBefore = CloneBitmap(bitmap);

        var scorer = new HarmonyScorer();
        var fitness = new HarmonyFitnessEvaluator(scorer);
        var harmonyBefore = fitness.Evaluate(bitmapBefore).Fitness;
        var qualityBefore = ScheduleQualityMetrics.Compute(bitmapBefore);

        var loop = BuildLoop(Seed);
        var result = loop.Run(bitmap);

        var harmonyAfter = result.Best.Fitness;
        var qualityAfter = ScheduleQualityMetrics.Compute(result.Best.Bitmap);
        var score = ResearchScore.Compute(qualityBefore, qualityAfter, harmonyBefore, harmonyAfter);
        return new RunResult(qualityBefore, qualityAfter, harmonyBefore, harmonyAfter, score);
    }

    private static HarmonizerEvolutionLoop BuildLoop(int seed)
    {
        var scorer = new HarmonyScorer();
        var domainValidator = new DomainAwareReplaceValidator(availability: null);
        var fitness = new HarmonyFitnessEvaluator(scorer);
        var stochasticMutation = new StochasticBitmapMutation(domainValidator);
        var config = new HarmonizerEvolutionConfig(
            PopulationSize: 6,
            MaxGenerations: 6,
            EliteCount: 2,
            TournamentSize: 3,
            StochasticMutationsPerOffspring: 2,
            StagnationGenerations: 4,
            Seed: seed);

        Func<int, HarmonizerConductor> conductorFactory = rowCount =>
        {
            var mutation = new ReplaceMutation(scorer, domainValidator);
            var blockSwap = new BlockSwapMutation(scorer, domainValidator);
            var emergency = new EmergencyUnlockManager(new EmergencyUnlockState(rowCount));
            return new HarmonizerConductor(scorer, mutation, emergency, blockSwapMutation: blockSwap);
        };

        return new HarmonizerEvolutionLoop(fitness, stochasticMutation, conductorFactory, config);
    }

    private static HarmonyBitmap CloneBitmap(HarmonyBitmap source)
    {
        var cells = new Cell[source.RowCount, source.DayCount];
        for (var r = 0; r < source.RowCount; r++)
        {
            for (var d = 0; d < source.DayCount; d++)
            {
                cells[r, d] = source.GetCell(r, d);
            }
        }
        return new HarmonyBitmap(source.Rows, source.Days, cells);
    }

    private static void ReportRun(string label, RunResult run)
    {
        var s = run.Score;
        TestContext.Progress.WriteLine($"-- {label} --");
        TestContext.Progress.WriteLine($"AUTORESEARCH_HARMONY {label}: {run.HarmonyBefore:F4} -> {run.HarmonyAfter:F4}");
        TestContext.Progress.WriteLine($"AUTORESEARCH_BLOCKS  {label}: {run.Before.TotalBlocks} -> {run.After.TotalBlocks}  (delta={s.FragmentationDelta:+0.00;-0.00;0.00})");
        TestContext.Progress.WriteLine($"AUTORESEARCH_AVGLEN  {label}: {run.Before.AvgBlockLength:F2} -> {run.After.AvgBlockLength:F2}  (delta={-s.BlockLengthDelta:+0.00;-0.00;0.00})");
        TestContext.Progress.WriteLine($"AUTORESEARCH_VIO     {label}: {run.Before.ConsecutiveDayViolations} -> {run.After.ConsecutiveDayViolations}");
        TestContext.Progress.WriteLine($"AUTORESEARCH_FAIR    {label}: {run.Before.TargetHoursAbsoluteDeviation:F2} -> {run.After.TargetHoursAbsoluteDeviation:F2}");
        TestContext.Progress.WriteLine($"AUTORESEARCH_LIE     {label}: {(s.ScorerLies ? "YES" : "no")}");
        TestContext.Progress.WriteLine(
            $"AUTORESEARCH_BREAKDOWN {label}: total={s.Total:F4}  frag={s.FragmentationPenalty:F3}  blockLen={s.BlockShorteningPenalty:F3}  vio={s.ViolationPenalty:F3}  fair={s.FairnessPenalty:F3}  lie={s.ScorerLiePenalty:F3}");
    }

    private static void PrintPerAgentDelta(RunResult run)
    {
        TestContext.Progress.WriteLine("-- Per-agent (workdays | blocks | avgLen | maxConsec) --");
        for (var i = 0; i < run.Before.Rows.Count; i++)
        {
            var b = run.Before.Rows[i];
            var a = run.After.Rows[i];
            TestContext.Progress.WriteLine(
                $"  {b.DisplayName,-22} {b.WorkDays,3} -> {a.WorkDays,3} | " +
                $"{b.Blocks,2} -> {a.Blocks,2} | " +
                $"{b.AvgBlockLength,4:F2} -> {a.AvgBlockLength,4:F2} | " +
                $"{b.MaxConsecutive,2} -> {a.MaxConsecutive,2}");
        }
    }
}
