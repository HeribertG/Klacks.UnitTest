// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

[TestFixture]
public class WarmStartRatioTests
{
    private static CoreWizardContext ContextWithSeed()
    {
        var date = new DateOnly(2026, 6, 1);
        return new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date.AddDays(29),
            WarmStartAssignments =
            [
                new CoreWarmStartAssignment(
                    AgentId: "A",
                    Date: date,
                    ShiftRefId: Guid.Empty,
                    StartAt: date.ToDateTime(new TimeOnly(8, 0)),
                    EndAt: date.ToDateTime(new TimeOnly(16, 0)),
                    TotalHours: 8m),
            ],
        };
    }

    private static CoreWizardContext ContextWithoutSeed()
    {
        var date = new DateOnly(2026, 6, 1);
        return new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date.AddDays(29),
        };
    }

    private sealed class CountingStrategy : ITokenPopulationStrategy
    {
        public int Invocations { get; private set; }

        public CoreScenario BuildScenario(CoreWizardContext context, Random rng)
        {
            Invocations++;
            return new CoreScenario { Id = Guid.NewGuid().ToString(), Tokens = [] };
        }
    }

    private sealed record Counters(
        CountingStrategy WarmStart,
        CountingStrategy Auction,
        CountingStrategy Coverage,
        CountingStrategy Greedy,
        CountingStrategy Random);

    private static (TokenPopulationBuilder Builder, Counters Counters) MakeBuilder()
    {
        var warmStart = new CountingStrategy();
        var auction = new CountingStrategy();
        var coverage = new CountingStrategy();
        var greedy = new CountingStrategy();
        var random = new CountingStrategy();
        var builder = new TokenPopulationBuilder(auction, coverage, greedy, random, warmStart);
        return (builder, new Counters(warmStart, auction, coverage, greedy, random));
    }

    [Test]
    public void BuildPopulation_DefaultsWithSeed_SplitsWarmStart10Auction15Coverage15Greedy5Random5()
    {
        var (builder, counters) = MakeBuilder();

        var population = builder.BuildPopulation(ContextWithSeed(), populationSize: 50, rng: new Random(0));

        counters.WarmStart.Invocations.ShouldBe(10);
        // Auction and coverage-first are deterministic: one base run each, the other 14 resp. 14
        // individuals are perturbed clones, so invocations no longer equal individuals.
        counters.Auction.Invocations.ShouldBe(1);
        counters.Coverage.Invocations.ShouldBe(1);
        counters.Greedy.Invocations.ShouldBe(5);
        counters.Random.Invocations.ShouldBe(5);
        population.Count().ShouldBe(50);
    }

    [Test]
    public void BuildPopulation_EmptySeed_WarmStartCountZeroAndAuctionKeepsFullShare()
    {
        var (builder, counters) = MakeBuilder();

        var population = builder.BuildPopulation(ContextWithoutSeed(), populationSize: 50, rng: new Random(0));

        counters.WarmStart.Invocations.ShouldBe(0);
        counters.Auction.Invocations.ShouldBe(1);
        population.Count().ShouldBe(50);
    }

    [Test]
    public void BuildPopulation_WarmStartRatioAboveAuctionRatio_ClampsAuctionToZeroWithoutNegativeCount()
    {
        var (builder, counters) = MakeBuilder();

        var population = builder.BuildPopulation(
            ContextWithSeed(),
            populationSize: 50,
            rng: new Random(0),
            auctionRatioOverride: 0.3,
            warmStartRatioOverride: 0.4);

        counters.WarmStart.Invocations.ShouldBe(20);
        counters.Auction.Invocations.ShouldBe(0);
        population.Count().ShouldBe(50);
    }

    [Test]
    public void BuildPopulation_WarmStartRatioOverOne_IsClampedSoPopulationIsNeverFullySeeded()
    {
        var (builder, counters) = MakeBuilder();

        var population = builder.BuildPopulation(
            ContextWithSeed(),
            populationSize: 50,
            rng: new Random(0),
            warmStartRatioOverride: 1.0);

        // Clamp MaxWarmStartRatio = 0.4 -> at most 20 of 50 individuals are warm-started.
        counters.WarmStart.Invocations.ShouldBe(20);
        counters.WarmStart.Invocations.ShouldBeLessThan(50);
        population.Count().ShouldBe(50);
    }
}
