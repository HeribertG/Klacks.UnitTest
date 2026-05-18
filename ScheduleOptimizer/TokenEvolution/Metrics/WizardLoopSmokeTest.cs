// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Diagnostics;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Metrics;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Metrics;

/// <summary>
/// One-off measurement to estimate how long a full TokenEvolutionLoop run takes with reduced
/// parameters. Drives the choice of seed count and population size for the multi-seed suite.
/// </summary>
[TestFixture, Explicit("Diagnostic only — prints timings, no assertions.")]
public sealed class WizardLoopSmokeTest
{
    [Test]
    public void Measure_BernHomogenLoop()
    {
        var ctx = WizardScenarioFixtures.BernFiveFullTimeHomogeneous();
        var loop = TokenEvolutionLoop.Create();
        var config = new TokenEvolutionConfig
        {
            PopulationSize = 20,
            MaxGenerations = 50,
            EarlyStopNoImprovementGenerations = 15,
            RandomSeed = 42,
        };
        var sw = Stopwatch.StartNew();
        var scenario = loop.Run(ctx, config);
        sw.Stop();
        var metrics = WizardMetricsCalculator.Compute(scenario, ctx, stage1EscalationCount: 0);
        TestContext.Out.WriteLine($"Bern homogen loop: {sw.ElapsedMilliseconds} ms");
        TestContext.Out.WriteLine($"  Coverage={metrics.CoveragePercent:P1} Target={metrics.TargetReachedPercent:P1} Gini={metrics.SlotGini:F3} Entropy={metrics.ShiftTypeEntropyAvg:F3} MaxBlock={metrics.MaxConsecutiveBlockLen}");
    }
}
