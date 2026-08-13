// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution;

[TestFixture]
public class TokenEvolutionLoopTests
{
    private static CoreAgent MakeAgent(string id, double fullTime = 40)
    {
        return new CoreAgent(
            Id: id,
            CurrentHours: 0,
            GuaranteedHours: 0,
            MaxConsecutiveDays: 6,
            MinRestHours: 11,
            Motivation: 0.5,
            MaxDailyHours: 10,
            MaxWeeklyHours: 50,
            MaxOptimalGap: 2)
        {
            FullTime = fullTime,
            PerformsShiftWork = true,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
        };
    }

    private static CoreWizardContext BuildContext(int agentCount, int dayCount)
    {
        var date = new DateOnly(2026, 4, 20);
        var agents = Enumerable.Range(0, agentCount)
            .Select(i => MakeAgent($"agent-{i}"))
            .ToList();
        var shifts = Enumerable.Range(0, dayCount)
            .Select(i => new CoreShift(Guid.NewGuid().ToString(), "FD", date.AddDays(i).ToString("yyyy-MM-dd"), "08:00", "16:00", 8, 1, 0))
            .ToList();

        return new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date.AddDays(dayCount - 1),
            Agents = agents,
            Shifts = shifts,
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMinPauseHours = 11,
            SchedulingMaxDailyHours = 10,
        };
    }

    [Test]
    public void Run_TrivialFixture_ReturnsFeasibleScenarioInFewGenerations()
    {
        var context = BuildContext(agentCount: 2, dayCount: 3);
        var config = new TokenEvolutionConfig
        {
            PopulationSize = 8,
            MaxGenerations = 20,
            EarlyStopNoImprovementGenerations = 5,
            RandomSeed = 42,
        };

        var sut = TokenEvolutionLoop.Create();
        var best = sut.Run(context, config);

        best.ShouldNotBeNull();
        best.Tokens.ShouldNotBeEmpty();
        best.FitnessStage0.ShouldBe(0);
    }

    [Test]
    public void Run_RespectsCancellation()
    {
        var context = BuildContext(agentCount: 3, dayCount: 14);
        var config = new TokenEvolutionConfig { PopulationSize = 8, MaxGenerations = 200 };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sut = TokenEvolutionLoop.Create();
        var act = () => sut.Run(context, config, cancellationToken: cts.Token);

        act.ShouldThrow<OperationCanceledException>();
    }

    [Test]
    public void Run_MaxRuntimeExpired_ReturnsBestSoFarInsteadOfThrowing()
    {
        var context = BuildContext(agentCount: 3, dayCount: 14);
        var config = new TokenEvolutionConfig
        {
            PopulationSize = 8,
            MaxGenerations = 200,
            EarlyStopNoImprovementGenerations = 200,
            RandomSeed = 42,
            MaxRuntime = TimeSpan.Zero,
        };

        var best = TokenEvolutionLoop.Create().Run(context, config);

        best.ShouldNotBeNull();
        best.Tokens.ShouldNotBeEmpty();
    }

    [Test]
    public void Run_ReportsProgressPerGeneration()
    {
        var context = BuildContext(agentCount: 2, dayCount: 3);
        var config = new TokenEvolutionConfig
        {
            PopulationSize = 6,
            MaxGenerations = 5,
            EarlyStopNoImprovementGenerations = 100,
            RandomSeed = 0,
        };

        var reports = new List<TokenEvolutionProgress>();
        var progress = new Progress<TokenEvolutionProgress>(reports.Add);

        TokenEvolutionLoop.Create().Run(context, config, progress);

        System.Threading.Thread.Sleep(200);
        reports.ShouldNotBeEmpty();
    }

    [Test]
    public void Run_PlaySequenceDominant_StaysFeasibleAndDeterministic()
    {
        // M7 play-sequence transaction: several single-operator draws on one child, only the end
        // state is compared. A dominant sequence weight must neither break feasibility nor the
        // seeded replay — the transaction draws all its randomness from the run's one rng.
        var context = BuildContext(agentCount: 3, dayCount: 7);
        TokenEvolutionConfig Config() => new()
        {
            PopulationSize = 8,
            MaxGenerations = 25,
            EarlyStopNoImprovementGenerations = 10,
            RandomSeed = 42,
            MutationRate = 1.0,
            MutationWeightPlaySequence = 5.0,
        };

        var first = TokenEvolutionLoop.Create().Run(context, Config());
        var second = TokenEvolutionLoop.Create().Run(context, Config());

        first.FitnessStage0.ShouldBe(0);
        static List<(string, DateOnly, Guid)> KeyOf(CoreScenario s) => s.Tokens
            .Select(t => (t.AgentId, t.Date, t.ShiftRefId))
            .OrderBy(x => x.AgentId).ThenBy(x => x.Date).ThenBy(x => x.ShiftRefId)
            .ToList();
        KeyOf(second).ShouldBe(KeyOf(first),
            "a seeded run with dominant play-sequence transactions must replay identically");
    }
}
