// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Diagnostics;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario2;
using NUnit.Framework;

namespace Klacks.UnitTest.Autofill.Scenarios;

/// <summary>
/// Names the evolution step that built every overlong work package a run ends up with. The guards pin
/// <c>packagesOverIdealLength</c>, but the metric is measured on the finished plan and no fitness
/// stage carries block length at a rank where the lexicographic comparison could reject it, so a
/// six-day block is invisible until the analyzer counts it. This fixture runs a scenario with the
/// engine's block diagnostics attached and reports, per package that survives into the final plan,
/// the first step that produced it.
/// <para>
/// Diagnosis only, never asserted, and read-only with respect to the run: the sink draws no randomness
/// and changes no plan, so the traced run is the same plan the guards measure.
/// </para>
/// </summary>
[TestFixture]
[Category("Autofill")]
[NonParallelizable]
[Explicit("Diagnosis: attributes overlong packages to the evolution step that built them")]
public sealed class AutofillOverlongBlockDiagnosticsTests
{
    private const string Scenario1Name = "scenario1";
    private const string Scenario1bName = "scenario1b";
    private const string Scenario2Name = "scenario2";

    private const string TracePrefix = "OVERLONG ";

    private const int MaxRawLines = 40;

    [TestCase(Scenario1Name, 42)]
    [TestCase(Scenario1Name, 43)]
    [TestCase(Scenario1Name, 44)]
    [TestCase(Scenario1bName, 42)]
    [TestCase(Scenario1bName, 43)]
    [TestCase(Scenario1bName, 44)]
    [TestCase(Scenario2Name, 42)]
    [TestCase(Scenario2Name, 43)]
    [TestCase(Scenario2Name, 44)]
    public void OverlongPackagesAreAttributedToTheStepThatBuiltThem(string scenario, int seed)
    {
        var definition = Build(scenario);
        var config = definition.Config with { RandomSeed = seed };

        var trace = new List<string>();
        var plan = TokenEvolutionLoop.Create().Run(
            definition.Context, config, blockDiagnostics: trace.Add);

        var final = OverlongBlockTrace.Overlong(plan, definition.Context)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        TestContext.Out.WriteLine($"=== {scenario} seed {seed} ===");
        TestContext.Out.WriteLine(
            $"overlong packages in the final plan: {final.Count.ToString(CultureInfo.InvariantCulture)}");

        foreach (var package in final)
        {
            var origin = trace.FirstOrDefault(line => line.EndsWith(package, StringComparison.Ordinal));
            TestContext.Out.WriteLine(
                origin is null
                    ? $"  {package} -> NOT TRACED (no step reported this package)"
                    : $"  {package} -> first built by {Stage(origin)}");
        }

        TestContext.Out.WriteLine(
            $"trace lines total: {trace.Count.ToString(CultureInfo.InvariantCulture)}");
        TestContext.Out.WriteLine("--- creations per step kind (step | count) ---");
        foreach (var group in trace
            .GroupBy(line => StepKind(Stage(line)))
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            TestContext.Out.WriteLine(
                $"  {group.Key} | {group.Count().ToString(CultureInfo.InvariantCulture)}");
        }

        TestContext.Out.WriteLine($"--- first {MaxRawLines.ToString(CultureInfo.InvariantCulture)} raw lines ---");
        foreach (var line in trace.Take(MaxRawLines))
        {
            TestContext.Out.WriteLine("  " + line);
        }
    }

    private static AutofillScenarioDefinition Build(string scenario) => scenario switch
    {
        Scenario1Name => CleanStart(AutofillSpecConstants.GuaranteedHours),
        Scenario1bName => CleanStart(AutofillSpecConstants.CalibrationGuaranteedHours),
        Scenario2Name => Scenario2CarryInFixture.Build(),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown scenario name."),
    };

    private static AutofillScenarioDefinition CleanStart(double guaranteedHours)
        => new AutofillScenarioBuilder()
            .WithPeriod(AutofillSpecConstants.PeriodFrom, AutofillSpecConstants.PeriodUntil)
            .WithEmployees(AutofillSpecConstants.EmployeeCount, guaranteedHours)
            .WithRandomSeed(AutofillSpecConstants.RandomSeed)
            .WithGaParameters(AutofillSpecConstants.PopulationSize, AutofillSpecConstants.MaxGenerations)
            .Build();

    private static string Stage(string traceLine)
    {
        var body = traceLine.StartsWith(TracePrefix, StringComparison.Ordinal)
            ? traceLine[TracePrefix.Length..]
            : traceLine;
        var separator = body.IndexOf(' ', StringComparison.Ordinal);
        return separator < 0 ? body : body[..separator];
    }

    private static string StepKind(string stage)
    {
        var dot = stage.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? stage : stage[(dot + 1)..];
    }
}
