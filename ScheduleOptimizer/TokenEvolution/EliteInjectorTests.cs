// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution;

[TestFixture]
public sealed class EliteInjectorTests
{
    /// <summary>Ranks by FitnessStage1 descending, mirroring "lower compare result means better".</summary>
    private sealed class Stage1Comparer : IComparer<CoreScenario>
    {
        public int Compare(CoreScenario? x, CoreScenario? y)
            => (y?.FitnessStage1 ?? 0).CompareTo(x?.FitnessStage1 ?? 0);
    }

    private static CoreScenario Scenario(string id, double stage1)
        => new() { Id = id, FitnessStage1 = stage1 };

    [Test]
    public void ReplaceWorst_SwapsOutTheWeakestIndividual()
    {
        var population = new List<CoreScenario>
        {
            Scenario("good", 0.9),
            Scenario("worst", 0.1),
            Scenario("middle", 0.5),
        };
        var elite = Scenario("elite", 0.95);

        EliteInjector.ReplaceWorst(population, elite, new Stage1Comparer());

        population.Count.ShouldBe(3);
        population.ShouldContain(elite);
        population.ShouldNotContain(s => s.Id == "worst");
        population.Select(s => s.Id).ShouldBe(new[] { "good", "elite", "middle" });
    }

    [Test]
    public void ReplaceWorst_EmptyPopulation_IsNoOp()
    {
        var population = new List<CoreScenario>();

        Should.NotThrow(() => EliteInjector.ReplaceWorst(population, Scenario("elite", 1.0), new Stage1Comparer()));

        population.ShouldBeEmpty();
    }

    [Test]
    public void ReplaceWorst_SingleIndividual_IsReplaced()
    {
        var population = new List<CoreScenario> { Scenario("only", 0.2) };
        var elite = Scenario("elite", 0.8);

        EliteInjector.ReplaceWorst(population, elite, new Stage1Comparer());

        population.Single().ShouldBe(elite);
    }
}
