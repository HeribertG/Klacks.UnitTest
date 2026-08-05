// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Evolution;

[TestFixture]
public class HarmonizerEvolutionLoopSmokeTests
{
    [Test]
    public void Run_DeterministicWithSeed_ReproducesSameResult()
    {
        var seedBitmap = EvolutionSmokeTestFixtures.BuildChaoticBitmap();
        var run1 = BuildLoop(seedSeed: 42).Run(BitmapCloner.Clone(seedBitmap));
        var run2 = BuildLoop(seedSeed: 42).Run(BitmapCloner.Clone(seedBitmap));

        run1.Best.Fitness.ShouldBe(run2.Best.Fitness);
    }

    [Test]
    public void Run_BestFitness_NeverDecreasesAcrossGenerations()
    {
        var seedBitmap = EvolutionSmokeTestFixtures.BuildChaoticBitmap();
        var loop = BuildLoop(seedSeed: 7);

        var result = loop.Run(seedBitmap);

        for (var i = 1; i < result.GenerationFitness.Count; i++)
        {
            result.GenerationFitness[i].ShouldBeGreaterThanOrEqualTo(result.GenerationFitness[i - 1] - 1e-9);
        }
    }

    [Test]
    public void Run_LockedCells_AreNeverMutated()
    {
        var seedBitmap = EvolutionSmokeTestFixtures.BuildChaoticBitmap();
        var lockedCell = new Cell(CellSymbol.Night, Guid.NewGuid(), [Guid.NewGuid()], true);
        seedBitmap.SetCell(0, 0, lockedCell);
        var loop = BuildLoop(seedSeed: 1);

        var result = loop.Run(seedBitmap);

        result.Best.Bitmap.GetCell(0, 0).ShouldBe(lockedCell);
    }

    [Test]
    public void Run_MaxRuntimeExpired_ReturnsSeedResultGracefully()
    {
        var seedBitmap = EvolutionSmokeTestFixtures.BuildChaoticBitmap();
        var loop = BuildLoop(seedSeed: 5, maxRuntime: TimeSpan.Zero);

        var result = loop.Run(seedBitmap);

        result.Best.ShouldNotBeNull();
        result.GenerationFitness.Count.ShouldBe(1);
    }

    [Test]
    public void Run_ResultNeverWorseThanSeed()
    {
        // Adversarial evaluator: only the untouched seed is optimal, every conductor move makes it worse.
        // Without the raw seed in the population the loop can only return a degraded plan.
        var seedBitmap = EvolutionSmokeTestFixtures.BuildChaoticBitmap();
        var reference = BitmapCloner.Clone(seedBitmap);
        var evaluator = new SeedFavouringEvaluator(reference);
        var seedFitness = evaluator.Evaluate(reference).Fitness;
        var loop = BuildLoop(seedSeed: 42, fitness: evaluator);

        var result = loop.Run(seedBitmap);

        result.Best.Fitness.ShouldBe(seedFitness, 1e-9);
        CountDifferences(reference, result.Best.Bitmap).ShouldBe(0);
    }

    [Test]
    public void Run_ConductorOnlyConfig_PopulationTwoZeroGenerations()
    {
        var seedBitmap = EvolutionSmokeTestFixtures.BuildChaoticBitmap();
        var evaluator = new HarmonyFitnessEvaluator(new HarmonyScorer());
        var seedFitness = evaluator.Evaluate(BitmapCloner.Clone(seedBitmap)).Fitness;
        var loop = BuildLoop(seedSeed: 42, populationSize: 2, maxGenerations: 0);

        var result = loop.Run(seedBitmap);

        result.GenerationFitness.Count.ShouldBe(1);
        result.Best.Fitness.ShouldBeGreaterThanOrEqualTo(seedFitness - 1e-9);
    }

    [Test]
    public void Run_PopulationOfOne_HoldsOnlyTheRawSeed()
    {
        var seedBitmap = EvolutionSmokeTestFixtures.BuildChaoticBitmap();
        var reference = BitmapCloner.Clone(seedBitmap);
        var loop = BuildLoop(seedSeed: 42, populationSize: 1, maxGenerations: 0);

        var result = loop.Run(seedBitmap);

        // No conductor pass fits into a population of one, so the seed comes back untouched.
        CountDifferences(reference, result.Best.Bitmap).ShouldBe(0);
        result.Best.ConductorTrace.RowTraces.Count.ShouldBe(reference.RowCount);
        result.Best.ConductorTrace.RowTraces.ShouldAllBe(t => t.MovesApplied == 0);
    }

    private static int CountDifferences(HarmonyBitmap left, HarmonyBitmap right)
    {
        var differences = 0;
        for (var r = 0; r < left.RowCount; r++)
        {
            for (var d = 0; d < left.DayCount; d++)
            {
                if (!Equals(left.GetCell(r, d), right.GetCell(r, d)))
                {
                    differences++;
                }
            }
        }
        return differences;
    }

    private sealed class SeedFavouringEvaluator : IBitmapFitnessEvaluator
    {
        private const double PenaltyPerChangedCell = 0.01;

        private readonly HarmonyBitmap _reference;

        public SeedFavouringEvaluator(HarmonyBitmap reference) => _reference = reference;

        public FitnessResult Evaluate(HarmonyBitmap bitmap)
        {
            var fitness = 1.0 - (CountDifferences(_reference, bitmap) * PenaltyPerChangedCell);
            var rowScores = new double[bitmap.RowCount];
            Array.Fill(rowScores, fitness);
            return new FitnessResult(fitness, rowScores);
        }
    }

    private static HarmonizerEvolutionLoop BuildLoop(
        int seedSeed,
        TimeSpan? maxRuntime = null,
        IBitmapFitnessEvaluator? fitness = null,
        int populationSize = 6,
        int maxGenerations = 8)
    {
        var scorer = new HarmonyScorer();
        var validator = new BitmapReplaceValidator();
        var evaluator = fitness ?? new HarmonyFitnessEvaluator(scorer);
        var stochasticMutation = new StochasticBitmapMutation(validator);
        var config = new HarmonizerEvolutionConfig(
            PopulationSize: populationSize,
            MaxGenerations: maxGenerations,
            EliteCount: 2,
            TournamentSize: 3,
            StochasticMutationsPerOffspring: 2,
            StagnationGenerations: 4,
            Seed: seedSeed,
            MaxRuntime: maxRuntime);

        Func<int, HarmonizerConductor> conductorFactory = rowCount =>
        {
            var mutation = new ReplaceMutation(scorer, validator);
            var emergencyState = new EmergencyUnlockState(rowCount);
            var emergency = new EmergencyUnlockManager(emergencyState);
            return new HarmonizerConductor(scorer, mutation, emergency);
        };

        return new HarmonizerEvolutionLoop(evaluator, stochasticMutation, conductorFactory, config);
    }
}
