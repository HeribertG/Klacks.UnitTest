// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

[TestFixture]
public class TokenPopulationBuilderTests
{
    private static CoreWizardContext MakeSampleContext()
    {
        var agent1 = new CoreAgent(
            Id: "A",
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
        };
        var agent2 = agent1 with { Id = "B", FullTime = 20 };

        var date = new DateOnly(2026, 4, 20);
        var shifts = Enumerable.Range(0, 5)
            .Select(i => new CoreShift(
                Guid.NewGuid().ToString(),
                "FD",
                date.AddDays(i).ToString("yyyy-MM-dd"),
                "08:00",
                "16:00",
                8,
                1,
                0))
            .ToList();

        return new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date.AddDays(4),
            Agents = [agent1, agent2],
            Shifts = shifts,
            SchedulingMaxConsecutiveDays = 6,
        };
    }

    [Test]
    public void BuildPopulation_GeneratesConfiguredPopulationSize()
    {
        var context = MakeSampleContext();
        var builder = new TokenPopulationBuilder(
            new Klacks.ScheduleOptimizer.TokenEvolution.Auction.AuctionTokenStrategy(),
            new CoverageFirstTokenStrategy(),
            new GreedyTokenStrategy(),
            new RandomTokenStrategy());

        var population = builder.BuildPopulation(context, populationSize: 10, rng: new Random(42));

        population.Count().ShouldBe(10);
        population.ShouldAllBe(s => s.Tokens.Count > 0);
    }

    [Test]
    public void BuildPopulation_DefaultRatios_Uses50AuctionPlus30Coverage10Greedy10Random()
    {
        var context = MakeSampleContext();
        var auctionInvocations = 0;
        var coverageInvocations = 0;
        var greedyInvocations = 0;
        var randomInvocations = 0;
        var auction = new CountingStrategy(() => auctionInvocations++);
        var coverageFirst = new CountingStrategy(() => coverageInvocations++);
        var greedy = new CountingStrategy(() => greedyInvocations++);
        var random = new CountingStrategy(() => randomInvocations++);
        var builder = new TokenPopulationBuilder(auction, coverageFirst, greedy, random);

        builder.BuildPopulation(context, populationSize: 10, rng: new Random(0));

        auctionInvocations.ShouldBe(5);
        coverageInvocations.ShouldBe(3);
        greedyInvocations.ShouldBe(1);
        randomInvocations.ShouldBe(1);
    }

    [Test]
    public void BuildPopulation_EveryScenarioContainsAllLockedTokens()
    {
        var sample = MakeSampleContext();
        var lockedDate = new DateOnly(2026, 4, 25);
        var lockedContext = new CoreWizardContext
        {
            PeriodFrom = sample.PeriodFrom,
            PeriodUntil = sample.PeriodUntil,
            Agents = sample.Agents,
            Shifts = sample.Shifts,
            LockedWorks =
            [
                new CoreLockedWork(
                    WorkId: "locked-1",
                    AgentId: "A",
                    Date: lockedDate,
                    ShiftTypeIndex: 0,
                    TotalHours: 8m,
                    StartAt: lockedDate.ToDateTime(new TimeOnly(8, 0)),
                    EndAt: lockedDate.ToDateTime(new TimeOnly(16, 0)),
                    ShiftRefId: Guid.NewGuid(),
                    LocationContext: null),
            ],
            SchedulingMaxConsecutiveDays = 6,
        };

        var builder = new TokenPopulationBuilder(
            new Klacks.ScheduleOptimizer.TokenEvolution.Auction.AuctionTokenStrategy(),
            new CoverageFirstTokenStrategy(),
            new GreedyTokenStrategy(),
            new RandomTokenStrategy());

        var population = builder.BuildPopulation(lockedContext, populationSize: 5, rng: new Random(0));

        population.ShouldAllBe(s => s.Tokens.Any(t => t.IsLocked && t.WorkIds.Contains("locked-1")));
    }

    private sealed class CountingStrategy : ITokenPopulationStrategy
    {
        private readonly Action _onInvoked;

        public CountingStrategy(Action onInvoked)
        {
            _onInvoked = onInvoked;
        }

        public CoreScenario BuildScenario(CoreWizardContext context, Random rng)
        {
            _onInvoked();
            return new CoreScenario { Id = Guid.NewGuid().ToString(), Tokens = [FakeToken()] };
        }

        private static CoreToken FakeToken()
        {
            var date = new DateOnly(2026, 4, 20);
            return new CoreToken(
                WorkIds: [],
                ShiftTypeIndex: 0,
                Date: date,
                TotalHours: 0,
                StartAt: date.ToDateTime(TimeOnly.MinValue),
                EndAt: date.ToDateTime(TimeOnly.MinValue),
                BlockId: Guid.NewGuid(),
                PositionInBlock: 0,
                IsLocked: false,
                LocationContext: null,
                ShiftRefId: Guid.Empty,
                AgentId: "fake");
        }
    }
}
