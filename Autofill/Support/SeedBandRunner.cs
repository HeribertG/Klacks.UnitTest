// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.UnitTest.Autofill.Analysis;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Support;

/// <summary>
/// Runs one scenario once more under a different random seed and measures the result. Everything but
/// the seed is taken over from the scenario the specification assertions ran on — same context, same
/// population size, same generation cap, sequential evaluation and no wall-clock budget — so the two
/// measurements differ in the seed and in nothing else.
/// <para>
/// A single run, deliberately: these seeds only widen the no-regression band and never carry an
/// assertion, so the determinism double run and the auction seed plan of
/// <see cref="DeterministicRunner"/> would only cost time here.
/// </para>
/// </summary>
public static class SeedBandRunner
{
    private const string RunLabelPrefix = "band-seed";

    /// <summary>
    /// Executes the scenario with the given seed and returns the measurement of that plan.
    /// </summary>
    /// <param name="definition">Scenario to run; its configuration is reused apart from the seed</param>
    /// <param name="seed">Random seed of this run</param>
    /// <param name="scenarioName">Scenario name, carried into the measurement</param>
    /// <param name="testName">Test name, carried into the measurement</param>
    public static AutofillMetrics Run(
        AutofillScenarioDefinition definition, int seed, string scenarioName, string testName)
    {
        var seeded = definition with { Config = definition.Config with { RandomSeed = seed } };
        var plan = TokenEvolutionLoop.Create().Run(seeded.Context, seeded.Config);

        return AutofillPlanAnalyzer.Analyze(
            plan, seeded, scenarioName, testName, RunLabelPrefix + seed.ToString(CultureInfo.InvariantCulture));
    }
}
