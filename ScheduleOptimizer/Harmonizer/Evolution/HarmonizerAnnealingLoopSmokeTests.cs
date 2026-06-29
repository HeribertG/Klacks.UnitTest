// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Evolution;

[TestFixture]
public class HarmonizerAnnealingLoopSmokeTests
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
    public void Run_BestFitness_NeverDecreasesAcrossSteps()
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
    public void Run_BestFitness_NeverWorseThanSeed()
    {
        var seedBitmap = EvolutionSmokeTestFixtures.BuildChaoticBitmap();
        var scorer = new HarmonyScorer();
        var seedFitness = new HarmonyFitnessEvaluator(scorer).Evaluate(BitmapCloner.Clone(seedBitmap)).Fitness;

        var result = BuildLoop(seedSeed: 3).Run(seedBitmap);

        result.Best.Fitness.ShouldBeGreaterThanOrEqualTo(seedFitness - 1e-9);
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
    public void Run_WithoutConductor_RunsAsPureMetropolisAndStaysValid()
    {
        var seedBitmap = EvolutionSmokeTestFixtures.BuildChaoticBitmap();
        var scorer = new HarmonyScorer();
        var seedFitness = new HarmonyFitnessEvaluator(scorer).Evaluate(BitmapCloner.Clone(seedBitmap)).Fitness;

        var result = BuildLoop(seedSeed: 11, withConductor: false, maxIterations: 200).Run(seedBitmap);

        result.Best.ShouldNotBeNull();
        result.Best.Fitness.ShouldBeGreaterThanOrEqualTo(seedFitness - 1e-9);
    }

    [Test]
    public void Run_NearZeroTemperature_BehavesGreedily()
    {
        var seedBitmap = EvolutionSmokeTestFixtures.BuildChaoticBitmap();
        var greedy = BuildLoop(seedSeed: 8, targetAcceptance: null, initialTemperature: 1e-6).Run(BitmapCloner.Clone(seedBitmap));
        var scorer = new HarmonyScorer();
        var seedFitness = new HarmonyFitnessEvaluator(scorer).Evaluate(BitmapCloner.Clone(seedBitmap)).Fitness;

        for (var i = 1; i < greedy.GenerationFitness.Count; i++)
        {
            greedy.GenerationFitness[i].ShouldBeGreaterThanOrEqualTo(greedy.GenerationFitness[i - 1] - 1e-9);
        }
        greedy.Best.Fitness.ShouldBeGreaterThanOrEqualTo(seedFitness - 1e-9);
    }

    private static HarmonizerAnnealingLoop BuildLoop(
        int seedSeed,
        bool withConductor = true,
        int maxIterations = 30,
        TimeSpan? maxRuntime = null,
        double? targetAcceptance = 0.8,
        double initialTemperature = 0.1)
    {
        var scorer = new HarmonyScorer();
        var validator = new BitmapReplaceValidator();
        var fitness = new HarmonyFitnessEvaluator(scorer);
        var stochasticMutation = new StochasticBitmapMutation(validator);
        var config = new HarmonizerAnnealingConfig(
            MaxIterations: maxIterations,
            InitialTemperature: initialTemperature,
            CoolingRate: 0.95,
            MovesPerStep: 2,
            RunConductorPerStep: withConductor,
            TargetInitialAcceptance: targetAcceptance,
            Seed: seedSeed,
            MaxRuntime: maxRuntime);

        Func<int, HarmonizerConductor> conductorFactory = rowCount =>
        {
            var mutation = new ReplaceMutation(scorer, validator);
            var emergencyState = new EmergencyUnlockState(rowCount);
            var emergency = new EmergencyUnlockManager(emergencyState);
            return new HarmonizerConductor(scorer, mutation, emergency);
        };

        return new HarmonizerAnnealingLoop(fitness, stochasticMutation, conductorFactory, config);
    }
}
