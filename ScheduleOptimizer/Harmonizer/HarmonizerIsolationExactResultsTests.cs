// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Research;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer;

/// <summary>
/// Pure in-memory isolation test for Wizard 2 (Harmonizer). Drives the engine core
/// (HarmonizerEvolutionLoop) directly with a fixed Seed and no MaxRuntime, bypassing
/// the DB-bound HarmonizerJobRunner so results are deterministic and reproducible.
/// Asserts only the scalar HarmonyFitness (elitism-guaranteed never to regress, and
/// hash-stable across processes) and prints the family quality metrics
/// (ScheduleQualityMetrics: fragmentation, consecutive-day violations, hours fairness)
/// before vs after so the run shows what the wizard concretely changed.
/// </summary>
[TestFixture]
public class HarmonizerIsolationExactResultsTests
{
    private const int Seed = 42;
    private const double FitnessTolerance = 1e-9;
    private const double ReproducibilityTolerance = 1e-12;

    [Test]
    public void Harmonizer_OnChaoticPlan_ImprovesQuality_AndReports()
    {
        var seedBitmap = BuildChaoticBitmap();
        var beforeBitmap = BitmapCloner.Clone(seedBitmap);

        var fitnessEvaluator = new HarmonyFitnessEvaluator(new HarmonyScorer());
        var beforeFitness = fitnessEvaluator.Evaluate(beforeBitmap).Fitness;
        var beforeQuality = ScheduleQualityMetrics.Compute(beforeBitmap);

        var loop = BuildLoop(Seed);
        var result = loop.Run(seedBitmap);

        var afterFitness = result.Best.Fitness;
        var afterQuality = ScheduleQualityMetrics.Compute(result.Best.Bitmap);

        ReportBeforeAfter(beforeFitness, afterFitness, beforeQuality, afterQuality);

        afterFitness.ShouldBeGreaterThanOrEqualTo(beforeFitness - FitnessTolerance);
    }

    [Test]
    public void Harmonizer_SameSeed_IsScalarReproducible()
    {
        var seedBitmap = BuildChaoticBitmap();

        var run1 = BuildLoop(Seed).Run(BitmapCloner.Clone(seedBitmap));
        var run2 = BuildLoop(Seed).Run(BitmapCloner.Clone(seedBitmap));

        run1.Best.Fitness.ShouldBe(run2.Best.Fitness, ReproducibilityTolerance);

        run1.GenerationFitness.Count.ShouldBe(run2.GenerationFitness.Count);
        for (var i = 0; i < run1.GenerationFitness.Count; i++)
        {
            run1.GenerationFitness[i].ShouldBe(run2.GenerationFitness[i], ReproducibilityTolerance);
        }
    }

    private static void ReportBeforeAfter(
        double beforeFitness,
        double afterFitness,
        ScheduleQualityReport before,
        ScheduleQualityReport after)
    {
        TestContext.Out.WriteLine("=== Wizard 2 (Harmonizer) — Isolation Exact Results (Seed: 42, no MaxRuntime) ===");
        TestContext.Out.WriteLine($"{"Metric",-32} {"BEFORE",18} {"AFTER",18}");
        TestContext.Out.WriteLine(new string('-', 70));
        WriteRow("HarmonyFitness (engine objective)", beforeFitness, afterFitness, "F12");
        WriteRow("TotalBlocks", before.TotalBlocks, after.TotalBlocks);
        WriteRow("AvgBlockLength", before.AvgBlockLength, after.AvgBlockLength, "F4");
        WriteRow("MaxConsecutiveDays", before.MaxConsecutiveDays, after.MaxConsecutiveDays);
        WriteRow("ConsecutiveDayViolations", before.ConsecutiveDayViolations, after.ConsecutiveDayViolations);
        WriteRow("TargetHoursStdDev", before.TargetHoursStdDev, after.TargetHoursStdDev);
        WriteRow("TargetHoursAbsoluteDeviation", before.TargetHoursAbsoluteDeviation, after.TargetHoursAbsoluteDeviation);
        TestContext.Out.WriteLine(new string('-', 70));
        TestContext.Out.WriteLine(
            "NOTE: only HarmonyFitness is asserted (elitism guarantees no regression). The");
        TestContext.Out.WriteLine(
            "family metrics are scorer-independent and may move either way — printed, not asserted.");
    }

    private static void WriteRow(string label, double before, double after, string format = "F2")
        => TestContext.Out.WriteLine(
            $"{label,-32} {before.ToString(format),18} {after.ToString(format),18}");

    private static void WriteRow(string label, int before, int after)
        => TestContext.Out.WriteLine($"{label,-32} {before,18} {after,18}");

    private static void WriteRow(string label, decimal before, decimal after)
        => TestContext.Out.WriteLine(
            $"{label,-32} {before.ToString("F2"),18} {after.ToString("F2"),18}");

    private static HarmonizerEvolutionLoop BuildLoop(int seed, TimeSpan? maxRuntime = null)
    {
        var scorer = new HarmonyScorer();
        var validator = new BitmapReplaceValidator();
        var fitness = new HarmonyFitnessEvaluator(scorer);
        var stochasticMutation = new StochasticBitmapMutation(validator);
        var config = new HarmonizerEvolutionConfig(
            PopulationSize: 6,
            MaxGenerations: 8,
            EliteCount: 2,
            TournamentSize: 3,
            StochasticMutationsPerOffspring: 2,
            StagnationGenerations: 4,
            Seed: seed,
            MaxRuntime: maxRuntime);

        Func<int, HarmonizerConductor> conductorFactory = rowCount =>
        {
            var mutation = new ReplaceMutation(scorer, validator);
            var emergencyState = new EmergencyUnlockState(rowCount);
            var emergency = new EmergencyUnlockManager(emergencyState);
            return new HarmonizerConductor(scorer, mutation, emergency);
        };

        return new HarmonizerEvolutionLoop(fitness, stochasticMutation, conductorFactory, config);
    }

    private static HarmonyBitmap BuildChaoticBitmap()
    {
        var agents = new List<BitmapAgent>
        {
            new("agent-0", "A0", 200m, new HashSet<CellSymbol>{ CellSymbol.Early }),
            new("agent-1", "A1", 150m, new HashSet<CellSymbol>{ CellSymbol.Late }),
            new("agent-2", "A2", 100m, new HashSet<CellSymbol>{ CellSymbol.Night }),
        };
        var startDate = new DateOnly(2026, 1, 1);
        var assignments = new List<BitmapAssignment>();
        var rng = new Random(99);
        var palette = new[] { CellSymbol.Early, CellSymbol.Late, CellSymbol.Night };
        for (var r = 0; r < agents.Count; r++)
        {
            for (var d = 0; d < 7; d++)
            {
                if (rng.NextDouble() < 0.6)
                {
                    assignments.Add(new BitmapAssignment(
                        agents[r].Id,
                        startDate.AddDays(d),
                        palette[rng.Next(palette.Length)],
                        Guid.NewGuid(),
                        [Guid.NewGuid()],
                        false));
                }
            }
        }
        var input = new BitmapInput(agents, startDate, startDate.AddDays(6), assignments);
        return BitmapBuilder.Build(input);
    }
}
