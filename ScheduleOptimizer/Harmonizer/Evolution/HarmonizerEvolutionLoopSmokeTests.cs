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
        var seedBitmap = BuildChaoticBitmap();
        var run1 = BuildLoop(seedSeed: 42).Run(BitmapCloner.Clone(seedBitmap));
        var run2 = BuildLoop(seedSeed: 42).Run(BitmapCloner.Clone(seedBitmap));

        run1.Best.Fitness.ShouldBe(run2.Best.Fitness);
    }

    [Test]
    public void Run_BestFitness_NeverDecreasesAcrossGenerations()
    {
        var seedBitmap = BuildChaoticBitmap();
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
        var seedBitmap = BuildChaoticBitmap();
        var lockedCell = new Cell(CellSymbol.Night, Guid.NewGuid(), [Guid.NewGuid()], true);
        seedBitmap.SetCell(0, 0, lockedCell);
        var loop = BuildLoop(seedSeed: 1);

        var result = loop.Run(seedBitmap);

        result.Best.Bitmap.GetCell(0, 0).ShouldBe(lockedCell);
    }

    private static HarmonizerEvolutionLoop BuildLoop(int seedSeed)
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
            Seed: seedSeed);

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
