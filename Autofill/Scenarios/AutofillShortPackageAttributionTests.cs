// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario2;
using NUnit.Framework;

namespace Klacks.UnitTest.Autofill.Scenarios;

/// <summary>
/// Names the evolution steps that create and dissolve SHORT packages (runs of at most two worked
/// days). The seed-splintering diagnosis of SPEC.md decision 13 showed the auction seeds are more
/// compact than the final plans, and no fitness stage carries package length at a rank the
/// lexicographic comparison rejects on — so this fixture runs a scenario with the engine's
/// short-package diagnostics attached and aggregates, per step kind, the net short-package drift,
/// plus the origin chains of the selected best plans whose count rose.
/// <para>
/// Diagnosis only, never asserted, and read-only with respect to the run: the sink draws no
/// randomness and changes no plan, so the traced run is the same plan the guards measure.
/// </para>
/// </summary>
[TestFixture]
[Category("Autofill")]
[NonParallelizable]
[Explicit("Diagnosis: attributes short-package drift to the evolution steps that caused it")]
public sealed class AutofillShortPackageAttributionTests
{
    private const string Scenario1Name = "scenario1";
    private const string Scenario1bName = "scenario1b";
    private const string Scenario2Name = "scenario2";

    private const string TracePrefix = "SHORTPKG ";

    private const string AddedMarker = "+";

    private const string RemovedMarker = "-";

    private const string CountMarker = "count=";

    private const string SelectedMarker = ".selected origin=";

    [TestCase(Scenario1Name, 42)]
    [TestCase(Scenario1bName, 42)]
    [TestCase(Scenario2Name, 42)]
    public void ShortPackageDriftIsAttributedToTheStepsThatCausedIt(string scenario, int seed)
    {
        var definition = Build(scenario);
        var config = definition.Config with { RandomSeed = seed };

        var trace = new List<string>();
        var plan = TokenEvolutionLoop.Create().Run(
            definition.Context, config, packageDiagnostics: trace.Add);

        TestContext.Out.WriteLine($"=== {scenario} seed {seed.ToString(CultureInfo.InvariantCulture)} ===");
        TestContext.Out.WriteLine($"trace lines total: {trace.Count.ToString(CultureInfo.InvariantCulture)}");

        var deltas = new Dictionary<string, (int Added, int Removed)>(StringComparer.Ordinal);
        var selected = new List<(int Generation, string Origin, int Count)>();
        var initCounts = new List<int>();
        var finalCount = -1;

        foreach (var raw in trace)
        {
            var line = raw.StartsWith(TracePrefix, StringComparison.Ordinal)
                ? raw[TracePrefix.Length..]
                : raw;

            if (line.StartsWith(AddedMarker + " ", StringComparison.Ordinal)
                || line.StartsWith(RemovedMarker + " ", StringComparison.Ordinal))
            {
                var added = line.StartsWith(AddedMarker, StringComparison.Ordinal);
                var stage = line[2..line.IndexOf(' ', 2)];
                var kind = StepKind(stage);
                var entry = deltas.GetValueOrDefault(kind);
                deltas[kind] = added ? (entry.Added + 1, entry.Removed) : (entry.Added, entry.Removed + 1);
                continue;
            }

            var countIndex = line.LastIndexOf(CountMarker, StringComparison.Ordinal);
            if (countIndex < 0)
            {
                continue;
            }

            var count = int.Parse(line[(countIndex + CountMarker.Length)..], CultureInfo.InvariantCulture);
            if (line.StartsWith("init[", StringComparison.Ordinal))
            {
                initCounts.Add(count);
            }
            else if (line.StartsWith("final", StringComparison.Ordinal))
            {
                finalCount = count;
            }
            else if (line.Contains(SelectedMarker, StringComparison.Ordinal))
            {
                var generation = int.Parse(
                    line["gen".Length..line.IndexOf(SelectedMarker, StringComparison.Ordinal)],
                    CultureInfo.InvariantCulture);
                var originStart = line.IndexOf(SelectedMarker, StringComparison.Ordinal) + SelectedMarker.Length;
                var origin = line[originStart..line.IndexOf(' ', originStart)];
                selected.Add((generation, origin, count));
            }
        }

        TestContext.Out.WriteLine(
            $"init counts: min={initCounts.Min().ToString(CultureInfo.InvariantCulture)} "
            + $"max={initCounts.Max().ToString(CultureInfo.InvariantCulture)} "
            + $"first={initCounts[0].ToString(CultureInfo.InvariantCulture)}; "
            + $"final count={finalCount.ToString(CultureInfo.InvariantCulture)}");

        TestContext.Out.WriteLine("--- net short-package drift per step kind (kind | added | removed | net) ---");
        foreach (var (kind, (added, removed)) in deltas
            .OrderByDescending(e => e.Value.Added - e.Value.Removed)
            .ThenBy(e => e.Key, StringComparer.Ordinal))
        {
            TestContext.Out.WriteLine(
                $"  {kind} | +{added.ToString(CultureInfo.InvariantCulture)} | "
                + $"-{removed.ToString(CultureInfo.InvariantCulture)} | "
                + $"{(added - removed).ToString(CultureInfo.InvariantCulture)}");
        }

        TestContext.Out.WriteLine("--- selected-best rises: origin of every generation whose count rose (origin | rises) ---");
        var rises = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 1; i < selected.Count; i++)
        {
            if (selected[i].Count > selected[i - 1].Count)
            {
                rises[selected[i].Origin] = rises.GetValueOrDefault(selected[i].Origin) + 1;
            }
        }

        foreach (var (origin, count) in rises
            .OrderByDescending(e => e.Value)
            .ThenBy(e => e.Key, StringComparer.Ordinal))
        {
            TestContext.Out.WriteLine(
                $"  {origin} | {count.ToString(CultureInfo.InvariantCulture)}");
        }

        if (selected.Count > 0)
        {
            TestContext.Out.WriteLine(
                $"selected timeline: first gen{selected[0].Generation.ToString(CultureInfo.InvariantCulture)}"
                + $"={selected[0].Count.ToString(CultureInfo.InvariantCulture)}, "
                + $"last gen{selected[^1].Generation.ToString(CultureInfo.InvariantCulture)}"
                + $"={selected[^1].Count.ToString(CultureInfo.InvariantCulture)}");
        }

        TestContext.Out.WriteLine(
            $"final plan tokens: {plan.Tokens.Count.ToString(CultureInfo.InvariantCulture)}");
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

    private static string StepKind(string stage)
    {
        var dot = stage.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? stage : stage[(dot + 1)..];
    }
}
