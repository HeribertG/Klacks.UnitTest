// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer;

[TestFixture]
public class HarmonizerEndToEndTests
{
    [Test]
    public void EndToEnd_ChaoticInputBecomesEqualOrBetter_NoLockedCellMutated()
    {
        var (bitmap, lockedRowIndex, lockedDayIndex, lockedCell) = BuildChaoticBitmapWithLockedCell();
        var loop = BuildLoop(seed: 42);
        var fitnessEvaluator = new HarmonyFitnessEvaluator(new HarmonyScorer());
        var initialFitness = fitnessEvaluator.Evaluate(bitmap).Fitness;

        var result = loop.Run(bitmap);

        result.Best.Fitness.ShouldBeGreaterThanOrEqualTo(initialFitness - 1e-9);
        result.Best.Bitmap.GetCell(lockedRowIndex, lockedDayIndex).ShouldBe(lockedCell);
    }

    [Test]
    public void EndToEnd_DomainAwareValidator_PreventsAssignmentOnUnavailableDay()
    {
        var (bitmap, agent2UnavailableDate, dayIndex) = BuildBitmapWithUnavailability();
        var availability = new Dictionary<(string AgentId, DateOnly Date), DayAvailability>
        {
            [("agent-0", agent2UnavailableDate)] = DayAvailability.AlwaysAvailable,
            [("agent-1", agent2UnavailableDate)] = new DayAvailability(WorksOnDay: false, HasFreeCommand: true, HasBreakBlocker: false),
        };
        var validator = new DomainAwareReplaceValidator(availability);
        var scorer = new HarmonyScorer();
        var mutation = new ReplaceMutation(scorer, validator);
        var emergency = new EmergencyUnlockManager(new EmergencyUnlockState(bitmap.RowCount));
        var conductor = new HarmonizerConductor(scorer, mutation, emergency);

        conductor.Run(bitmap);

        var receivedCellOnAgent1 = bitmap.GetCell(1, dayIndex);
        receivedCellOnAgent1.Symbol.ShouldBe(CellSymbol.Free, "swap onto unavailable agent must not happen");
    }

    private static HarmonizerEvolutionLoop BuildLoop(int seed)
    {
        var scorer = new HarmonyScorer();
        var validator = new BitmapReplaceValidator();
        var fitness = new HarmonyFitnessEvaluator(scorer);
        var stochasticMutation = new StochasticBitmapMutation(validator);
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
            var mutation = new ReplaceMutation(scorer, validator);
            var emergency = new EmergencyUnlockManager(new EmergencyUnlockState(rowCount));
            return new HarmonizerConductor(scorer, mutation, emergency);
        };

        return new HarmonizerEvolutionLoop(fitness, stochasticMutation, conductorFactory, config);
    }

    private static (HarmonyBitmap Bitmap, int LockedRow, int LockedDay, Cell LockedCell) BuildChaoticBitmapWithLockedCell()
    {
        var agents = new List<BitmapAgent>
        {
            new("a", "A", 200m, new HashSet<CellSymbol>()),
            new("b", "B", 150m, new HashSet<CellSymbol>()),
            new("c", "C", 100m, new HashSet<CellSymbol>()),
        };
        var startDate = new DateOnly(2026, 1, 1);
        var palette = new[] { CellSymbol.Early, CellSymbol.Late, CellSymbol.Night };
        var assignments = new List<BitmapAssignment>();
        var rng = new Random(7);
        for (var r = 0; r < agents.Count; r++)
        {
            for (var d = 0; d < 10; d++)
            {
                if (rng.NextDouble() < 0.7)
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
        var bitmap = BitmapBuilder.Build(new BitmapInput(agents, startDate, startDate.AddDays(9), assignments));

        var lockedCell = new Cell(CellSymbol.Night, Guid.NewGuid(), [Guid.NewGuid()], true);
        bitmap.SetCell(0, 5, lockedCell);
        return (bitmap, 0, 5, lockedCell);
    }

    private static (HarmonyBitmap Bitmap, DateOnly Date, int DayIndex) BuildBitmapWithUnavailability()
    {
        var agents = new List<BitmapAgent>
        {
            new("agent-0", "A0", 100m, new HashSet<CellSymbol>()),
            new("agent-1", "A1", 100m, new HashSet<CellSymbol>()),
        };
        var startDate = new DateOnly(2026, 1, 5);
        var swapDay = startDate.AddDays(1);
        var assignments = new List<BitmapAssignment>
        {
            new("agent-0", swapDay, CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], false,
                swapDay.ToDateTime(new TimeOnly(7, 0)), swapDay.ToDateTime(new TimeOnly(15, 0)), 8m),
        };
        var bitmap = BitmapBuilder.Build(new BitmapInput(agents, startDate, startDate.AddDays(2), assignments));
        return (bitmap, swapDay, 1);
    }
}
