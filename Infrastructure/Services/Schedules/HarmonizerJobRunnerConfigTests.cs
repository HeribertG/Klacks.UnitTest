// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Configuration;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

/// <summary>
/// Pins the engine-mode contract of Wizard 2: conductor-only is the default, the genetic loop is opt-in.
/// </summary>
[TestFixture]
public class HarmonizerJobRunnerConfigTests
{
    private const int Seed = 12345;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(90);

    [Test]
    public void HarmonizerOptions_DefaultsToConductorOnly()
    {
        new HarmonizerOptions().UseEvolution.ShouldBeFalse();
    }

    [Test]
    public void BuildEvolutionConfig_ConductorOnly_UsesSeedPlusOnePassAndNoGenerations()
    {
        var config = HarmonizerJobRunner.BuildEvolutionConfig(useEvolution: false, Budget, Seed);

        config.PopulationSize.ShouldBe(2);
        config.MaxGenerations.ShouldBe(0);
        config.MaxRuntime.ShouldBe(Budget);
        config.Seed.ShouldBe(Seed);
    }

    [Test]
    public void BuildEvolutionConfig_Evolution_KeepsGeneticDefaults()
    {
        var defaults = new HarmonizerEvolutionConfig();

        var config = HarmonizerJobRunner.BuildEvolutionConfig(useEvolution: true, Budget, Seed);

        config.PopulationSize.ShouldBe(defaults.PopulationSize);
        config.MaxGenerations.ShouldBe(defaults.MaxGenerations);
        config.MaxRuntime.ShouldBe(Budget);
        // Without a recorded seed a reported harmonizer result cannot be replayed.
        config.Seed.ShouldBe(Seed);
    }
}
