// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.TokenEvolution.Auction;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

/// <summary>
/// The rotation start takes its share out of the random remainder; without the strategy the population is built as
/// before.
/// </summary>
[TestFixture]
public sealed class TokenPopulationBuilderRotationTests
{
    [Test]
    public void WithRotation_ThePopulationKeepsItsSize()
    {
        var context = RotationTestContexts.Scenario4();
        var builder = new TokenPopulationBuilder(
            new AuctionTokenStrategy(),
            new CoverageFirstTokenStrategy(),
            new GreedyTokenStrategy(),
            new RandomTokenStrategy(),
            new WarmStartTokenStrategy(),
            new RotationTemplateTokenStrategy())
        {
            InitRotationTemplateRatio = 0.2,
        };

        var population = builder.BuildPopulation(context, populationSize: 10, rng: new Random(42));

        population.Count.ShouldBe(10);
    }

    [Test]
    public void DefaultRatio_IsOneTenth()
    {
        var builder = new TokenPopulationBuilder(
            new AuctionTokenStrategy(),
            new CoverageFirstTokenStrategy(),
            new GreedyTokenStrategy(),
            new RandomTokenStrategy(),
            new WarmStartTokenStrategy());

        builder.InitRotationTemplateRatio.ShouldBe(0.1);
    }

    [Test]
    public void RotationNotApplicable_PopulationIsExactlyTheOneWithoutIt()
    {
        var context = RotationTestContexts.SmallTeam();

        var with = Build(new RotationTemplateTokenStrategy()).BuildPopulation(context, populationSize: 10, rng: new Random(42));
        var without = Build(null).BuildPopulation(context, populationSize: 10, rng: new Random(42));

        with.Select(Signature).ShouldBe(without.Select(Signature));
    }

    private static TokenPopulationBuilder Build(ITokenPopulationStrategy? rotation) => new(
        new AuctionTokenStrategy(),
        new CoverageFirstTokenStrategy(),
        new GreedyTokenStrategy(),
        new RandomTokenStrategy(),
        new WarmStartTokenStrategy(),
        rotation);

    private static string Signature(Klacks.ScheduleOptimizer.Models.CoreScenario scenario)
        => string.Join(";", scenario.Tokens
            .OrderBy(t => t.AgentId, StringComparer.Ordinal).ThenBy(t => t.Date).ThenBy(t => t.StartAt)
            .Select(t => $"{t.AgentId}@{t.Date:yyyy-MM-dd}:{t.ShiftTypeIndex}"));
}
