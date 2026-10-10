// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario2;
using NUnit.Framework;

namespace Klacks.UnitTest.Autofill.Scenarios;

/// <summary>
/// Censuses the rotation balancer's end pass on real runs: how many same-day block pairs exist, how many survive the
/// slot filter, improve the rotation in the spec's order, raise the block order and pass the lexicographic comparison,
/// with detail lines for the swaps the comparison refused. Diagnosis only, never asserted.
/// </summary>
[TestFixture]
[Category("Autofill")]
[NonParallelizable]
[Explicit("Diagnosis: censuses the rotation balancer's end pass on scenario 1b and scenario 2")]
public sealed class RotationBalancerCensusTests
{
    private const string CensusMarker = "ROTATION-BALANCE";
    private const int Scenario1bSeed = 42;
    private const int Scenario2Seed = 44;

    [Test]
    public void RotationBalanceCensusOnScenario1bAndScenario2()
    {
        Census("S1b", CleanStart(AutofillSpecConstants.CalibrationGuaranteedHours), Scenario1bSeed);
        Census("S2", Scenario2CarryInFixture.Build(), Scenario2Seed);
    }

    private static void Census(string name, AutofillScenarioDefinition definition, int seed)
    {
        var config = definition.Config with { RandomSeed = seed };
        var lines = new List<string>();
        TokenEvolutionLoop.Create().Run(definition.Context, config, trace: line =>
        {
            if (line.StartsWith(CensusMarker, StringComparison.Ordinal))
            {
                lines.Add(line);
            }
        });

        TestContext.Out.WriteLine($"=== rotation balance census, {name}, seed {seed} ===");
        foreach (var line in lines)
        {
            TestContext.Out.WriteLine(line);
        }
    }

    private static AutofillScenarioDefinition CleanStart(double guaranteedHours)
        => new AutofillScenarioBuilder()
            .WithPeriod(AutofillSpecConstants.PeriodFrom, AutofillSpecConstants.PeriodUntil)
            .WithEmployees(AutofillSpecConstants.EmployeeCount, guaranteedHours)
            .Build();
}
