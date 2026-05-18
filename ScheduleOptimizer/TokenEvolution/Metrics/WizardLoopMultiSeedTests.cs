// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Metrics;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Metrics;

/// <summary>
/// Multi-seed regression suite for the full TokenEvolutionLoop. The UI WizardJobRunner uses
/// Guid.NewGuid().GetHashCode() as RandomSeed for each run — single-run measurements are
/// therefore noisy. This suite runs N seeds per scenario, aggregates mean and standard deviation
/// per metric and compares against a frozen aggregate baseline.
/// </summary>
/// <remarks>
/// Run <see cref="GenerateLoopBaseline"/> explicitly to regenerate the baseline after a deliberate
/// tuning change. Loop is configured with a small population to keep total runtime under one minute.
/// </remarks>
[TestFixture]
public sealed class WizardLoopMultiSeedTests
{
    private const int SeedCount = 5;
    private static readonly int[] Seeds = [11, 23, 37, 53, 71];

    private const double CoverageMeanMinDelta = -0.02;
    private const double TargetReachedMeanMinDelta = -0.10;
    private const double GiniMeanMaxDelta = 0.05;
    private const double EntropyMeanMinDelta = -0.10;
    private const int MaxBlockMaxAllowed = 7;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static readonly Dictionary<string, Func<CoreWizardContext>> Scenarios = new(StringComparer.Ordinal)
    {
        ["BernFiveFullTimeHomogeneous"] = WizardScenarioFixtures.BernFiveFullTimeHomogeneous,
        ["HeterogeneousMix"] = WizardScenarioFixtures.HeterogeneousMix,
    };

    [TestCase("BernFiveFullTimeHomogeneous")]
    [TestCase("HeterogeneousMix")]
    public void LoopScenario_AggregateMetricsWithinBaseline(string scenarioName)
    {
        var baseline = LoadBaseline();
        Assert.That(baseline.ContainsKey(scenarioName), Is.True,
            $"Loop baseline missing for '{scenarioName}'. Run GenerateLoopBaseline.");

        var actual = RunMultiSeed(scenarioName);
        AssertWithinTolerance(scenarioName, actual, baseline[scenarioName]);
    }

    [Test, Explicit("Run manually after a deliberate tuning change; commit the regenerated JSON.")]
    public void GenerateLoopBaseline()
    {
        var snapshot = new Dictionary<string, WizardMetricsAggregate>(StringComparer.Ordinal);
        foreach (var name in Scenarios.Keys)
        {
            snapshot[name] = RunMultiSeed(name);
        }
        File.WriteAllText(BaselinePath(), JsonSerializer.Serialize(snapshot, JsonOpts));
        TestContext.Out.WriteLine($"Loop baseline written: {BaselinePath()}");
    }

    [Test, Explicit("Diagnostic dump — prints per-seed snapshots for inspection.")]
    public void DumpPerSeedSnapshots()
    {
        foreach (var name in Scenarios.Keys)
        {
            TestContext.Out.WriteLine($"\n--- {name} ---");
            var ctx = Scenarios[name]();
            foreach (var seed in Seeds)
            {
                var snap = RunOne(ctx, seed);
                TestContext.Out.WriteLine(
                    $"  seed={seed,4}  cov={snap.CoveragePercent:P1}  target={snap.TargetReachedPercent:P1}  gini={snap.SlotGini:F3}  entropy={snap.ShiftTypeEntropyAvg:F3}  maxBlock={snap.MaxConsecutiveBlockLen}");
            }
            var agg = RunMultiSeed(name);
            TestContext.Out.WriteLine(
                $"  AGG mean cov={agg.CoverageMean:P1}+-{agg.CoverageStdDev:P1}  target={agg.TargetReachedMean:P1}+-{agg.TargetReachedStdDev:P1}  gini={agg.SlotGiniMean:F3}+-{agg.SlotGiniStdDev:F3}  entropy={agg.ShiftTypeEntropyMean:F3}+-{agg.ShiftTypeEntropyStdDev:F3}  maxBlock_avg={agg.MaxConsecutiveBlockLenMean:F1} max={agg.MaxConsecutiveBlockLenMax}");
        }
    }

    private static WizardMetricsAggregate RunMultiSeed(string scenarioName)
    {
        var ctx = Scenarios[scenarioName]();
        var snapshots = new List<WizardMetricsSnapshot>(SeedCount);
        foreach (var seed in Seeds)
        {
            snapshots.Add(RunOne(ctx, seed));
        }
        return WizardMetricsAggregate.FromSnapshots(snapshots);
    }

    private static WizardMetricsSnapshot RunOne(CoreWizardContext ctx, int seed)
    {
        var loop = TokenEvolutionLoop.Create();
        var config = new TokenEvolutionConfig
        {
            PopulationSize = 20,
            MaxGenerations = 50,
            EarlyStopNoImprovementGenerations = 15,
            RandomSeed = seed,
        };
        var scenario = loop.Run(ctx, config);
        return WizardMetricsCalculator.Compute(scenario, ctx, stage1EscalationCount: 0);
    }

    private static void AssertWithinTolerance(string scenario, WizardMetricsAggregate actual, WizardMetricsAggregate baseline)
    {
        var coverageDelta = actual.CoverageMean - baseline.CoverageMean;
        Assert.That(coverageDelta, Is.GreaterThanOrEqualTo(CoverageMeanMinDelta),
            $"{scenario}: Coverage mean dropped from {baseline.CoverageMean:P1} to {actual.CoverageMean:P1}");

        var targetDelta = actual.TargetReachedMean - baseline.TargetReachedMean;
        Assert.That(targetDelta, Is.GreaterThanOrEqualTo(TargetReachedMeanMinDelta),
            $"{scenario}: TargetReached mean dropped from {baseline.TargetReachedMean:P1} to {actual.TargetReachedMean:P1}");

        var giniDelta = actual.SlotGiniMean - baseline.SlotGiniMean;
        Assert.That(giniDelta, Is.LessThanOrEqualTo(GiniMeanMaxDelta),
            $"{scenario}: Gini mean grew from {baseline.SlotGiniMean:F3} to {actual.SlotGiniMean:F3} (less fair)");

        var entropyDelta = actual.ShiftTypeEntropyMean - baseline.ShiftTypeEntropyMean;
        Assert.That(entropyDelta, Is.GreaterThanOrEqualTo(EntropyMeanMinDelta),
            $"{scenario}: Entropy mean dropped from {baseline.ShiftTypeEntropyMean:F3} to {actual.ShiftTypeEntropyMean:F3}");

        Assert.That(actual.MaxConsecutiveBlockLenMax, Is.LessThanOrEqualTo(MaxBlockMaxAllowed),
            $"{scenario}: worst-case MaxConsecutiveBlock = {actual.MaxConsecutiveBlockLenMax} exceeds allowed {MaxBlockMaxAllowed}");
    }

    private static Dictionary<string, WizardMetricsAggregate> LoadBaseline()
    {
        var path = BaselinePath();
        if (!File.Exists(path))
        {
            return new Dictionary<string, WizardMetricsAggregate>(StringComparer.Ordinal);
        }
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, WizardMetricsAggregate>>(json, JsonOpts)
            ?? new Dictionary<string, WizardMetricsAggregate>(StringComparer.Ordinal);
    }

    private static string BaselinePath([CallerFilePath] string thisFile = "")
    {
        var dir = Path.GetDirectoryName(thisFile)!;
        return Path.Combine(dir, "wizard-loop-multi-seed-baseline.json");
    }
}
