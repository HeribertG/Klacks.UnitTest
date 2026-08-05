// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution;

/// <summary>
/// Scoring a generation now runs on several threads. That is only allowed because it cannot change the
/// outcome: breeding stays sequential on the single seeded random source, evaluation draws no
/// randomness, and each scenario is written by exactly one thread. These tests hold the engine to that
/// claim by running the same seed sequentially and in parallel and demanding identical numbers - a
/// silent divergence would otherwise only surface as an irreproducible plan in production.
/// </summary>
[TestFixture]
public sealed class ParallelEvaluationDeterminismTests
{
    private const int Seed = 4711;
    private static readonly DateOnly PeriodStart = new(2026, 4, 20);

    [Test]
    public void Run_SequentialAndParallelEvaluation_ProduceIdenticalFitness()
    {
        var sequential = TokenEvolutionLoop.Create().Run(BuildContext(), Config(parallelism: 1));
        var parallel = TokenEvolutionLoop.Create().Run(BuildContext(), Config(parallelism: 4));

        parallel.FitnessStage0.ShouldBe(sequential.FitnessStage0);
        parallel.FitnessStage1.ShouldBe(sequential.FitnessStage1);
        parallel.FitnessStage2.ShouldBe(sequential.FitnessStage2);
        parallel.FitnessStage3.ShouldBe(sequential.FitnessStage3);
        parallel.FitnessStage4.ShouldBe(sequential.FitnessStage4);
        parallel.Fitness.ShouldBe(sequential.Fitness);
    }

    [Test]
    public void Run_SequentialAndParallelEvaluation_ProduceTheIdenticalAssignment()
    {
        var sequential = TokenEvolutionLoop.Create().Run(BuildContext(), Config(parallelism: 1));
        var parallel = TokenEvolutionLoop.Create().Run(BuildContext(), Config(parallelism: 4));

        Fingerprint(parallel).ShouldBe(Fingerprint(sequential));
    }

    [Test]
    public void Run_DefaultParallelism_MatchesTheSequentialReference()
    {
        var sequential = TokenEvolutionLoop.Create().Run(BuildContext(), Config(parallelism: 1));
        var byDefault = TokenEvolutionLoop.Create().Run(BuildContext(), Config(parallelism: 0));

        Fingerprint(byDefault).ShouldBe(Fingerprint(sequential));
        byDefault.Fitness.ShouldBe(sequential.Fitness);
    }

    private static IReadOnlyList<string> Fingerprint(CoreScenario scenario) => scenario.Tokens
        .Select(t => $"{t.AgentId}|{t.Date:yyyy-MM-dd}|{t.ShiftRefId}|{t.StartAt:O}|{t.TotalHours}")
        .OrderBy(s => s, StringComparer.Ordinal)
        .ToList();

    private static TokenEvolutionConfig Config(int parallelism) => new()
    {
        PopulationSize = 12,
        MaxGenerations = 8,
        EarlyStopNoImprovementGenerations = 8,
        RandomSeed = Seed,
        EvaluationParallelism = parallelism,
    };

    private static CoreWizardContext BuildContext()
    {
        const int agentCount = 6;
        const int dayCount = 10;

        var agents = Enumerable.Range(0, agentCount)
            .Select(i => new CoreAgent(
                Id: $"agent-{i}",
                CurrentHours: 0,
                GuaranteedHours: 0,
                MaxConsecutiveDays: 6,
                MinRestHours: 11,
                Motivation: 0.5,
                MaxDailyHours: 10,
                MaxWeeklyHours: 50,
                MaxOptimalGap: 2)
            {
                FullTime = 40,
                PerformsShiftWork = true,
                WorkOnMonday = true,
                WorkOnTuesday = true,
                WorkOnWednesday = true,
                WorkOnThursday = true,
                WorkOnFriday = true,
                WorkOnSaturday = true,
                WorkOnSunday = true,
            })
            .ToList();

        // Fixed shift ids so both runs see the identical context, not just an equivalent one.
        var shifts = Enumerable.Range(0, dayCount)
            .Select(i => new CoreShift(
                new Guid($"00000000-0000-0000-0000-{i:D12}").ToString(),
                "FD",
                PeriodStart.AddDays(i).ToString("yyyy-MM-dd"),
                "08:00",
                "16:00",
                8,
                2,
                0))
            .ToList();

        return new CoreWizardContext
        {
            PeriodFrom = PeriodStart,
            PeriodUntil = PeriodStart.AddDays(dayCount - 1),
            Agents = agents,
            Shifts = shifts,
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMinPauseHours = 11,
            SchedulingMaxDailyHours = 10,
        };
    }
}
