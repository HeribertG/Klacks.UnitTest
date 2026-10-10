// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario2;
using NUnit.Framework;

namespace Klacks.UnitTest.Autofill.Analysis;

/// <summary>
/// Diagnostic, not a guard: measures the round-4 rotation metrics on the start plans each Wizard 1 strategy builds,
/// before and after the top-down handover, without the genetic search. A change to a producer is then attributable
/// in seconds, while the full scenario runs mix it with 600 generations of search noise.
/// </summary>
[TestFixture]
[Explicit("Diagnostic: prints rotation metrics of the start plans, asserts nothing.")]
public sealed class RotationProducerDiagnosticsTests
{
    private const int Seed = AutofillSpecConstants.RandomSeed;

    private static readonly (string Name, Func<AutofillScenarioDefinition> Build)[] Scenarios =
    [
        ("S1", () => CleanStart(AutofillSpecConstants.GuaranteedHours)),
        ("S1b", () => CleanStart(AutofillSpecConstants.CalibrationGuaranteedHours)),
        ("S2", Scenario2CarryInFixture.Build),
    ];

    private static readonly (string Name, ITokenPopulationStrategy Strategy)[] Strategies =
    [
        ("auction", new AuctionTokenStrategy()),
        ("coverageFirst", new CoverageFirstTokenStrategy()),
        ("greedy", new GreedyTokenStrategy()),
    ];

    [Test]
    public void PrintStartPlanRotation()
    {
        var handover = new TopDownHandover();
        foreach (var (scenarioName, build) in Scenarios)
        {
            var definition = build();
            foreach (var (strategyName, strategy) in Strategies)
            {
                var plan = strategy.BuildScenario(definition.Context, new Random(Seed));
                Print(scenarioName, strategyName, "raw", plan, definition);
                Print(scenarioName, strategyName, "handover", handover.Apply(plan, definition.Context), definition);
            }
        }
    }

    private static void Print(string scenario, string strategy, string stage, CoreScenario plan, AutofillScenarioDefinition definition)
    {
        var metrics = AutofillPlanAnalyzer.Analyze(plan, definition, scenario, strategy, stage);
        var spec = metrics.Rotation.Spec;
        TestContext.Out.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "PRODUCER {0,-4} {1,-13} {2,-8} ideal={3:0.000} trans={4,2} unforced={5,2} purity={6:0.000} mixed={7,2} filled={8}/{9}",
            scenario, strategy, stage, spec.IdealTransitionRate, spec.TransitionCount, spec.UnforcedDeviations,
            spec.BlockPurity, metrics.Packages.MixedTypeCount, metrics.Coverage.FilledShifts, metrics.Coverage.TotalRequiredShifts));
    }

    private static AutofillScenarioDefinition CleanStart(double guaranteedHours)
        => new AutofillScenarioBuilder()
            .WithPeriod(AutofillSpecConstants.PeriodFrom, AutofillSpecConstants.PeriodUntil)
            .WithEmployees(AutofillSpecConstants.EmployeeCount, guaranteedHours)
            .Build();
}
