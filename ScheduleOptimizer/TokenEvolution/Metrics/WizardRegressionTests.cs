// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Agent;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Conductor;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Controller;
using Klacks.ScheduleOptimizer.TokenEvolution.Metrics;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Metrics;

/// <summary>
/// Pareto-style regression suite. Each scenario runs the SlotAuctioneer with a fixed seed and
/// compares the resulting metric vector against a frozen baseline. A change is only accepted if
/// every dimension stays within tolerance — coverage is never allowed to drop, fairness/entropy
/// are bounded both ways. Detects tuning loops where one metric improves while another silently
/// regresses.
/// </summary>
/// <remarks>To refresh the baseline after a deliberate change, run the explicit
/// <see cref="GenerateBaseline"/> test once and commit the updated JSON.</remarks>
[TestFixture]
public sealed class WizardRegressionTests
{
    private const int Seed = 42;

    private const double CoverageMinDelta = -0.0001;
    private const double TargetReachedMinDelta = -0.05;
    private const double GiniMaxDelta = 0.05;
    private const double EntropyMinDelta = -0.10;
    private const int EscalationMaxDelta = 2;
    private const int MaxBlockMaxDelta = 1;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static readonly Dictionary<string, Func<CoreWizardContext>> Scenarios = new(StringComparer.Ordinal)
    {
        ["BernFiveFullTimeHomogeneous"] = WizardScenarioFixtures.BernFiveFullTimeHomogeneous,
        ["HeterogeneousMix"] = WizardScenarioFixtures.HeterogeneousMix,
        ["BoundaryWithPriorWorks"] = WizardScenarioFixtures.BoundaryWithPriorWorks,
    };

    [TestCase("BernFiveFullTimeHomogeneous")]
    [TestCase("HeterogeneousMix")]
    [TestCase("BoundaryWithPriorWorks")]
    public void Scenario_MetricsWithinBaselineTolerance(string scenarioName)
    {
        var baseline = LoadBaseline();
        baseline.ShouldContainKey(scenarioName,
            $"Baseline entry missing for '{scenarioName}'. Run the explicit GenerateBaseline test and commit the JSON.");

        var actual = RunAndMeasure(scenarioName);
        AssertWithinTolerance(scenarioName, actual, baseline[scenarioName]);
    }

    [Test, Explicit("Run this manually after a deliberate tuning change, then commit the regenerated JSON.")]
    public void GenerateBaseline()
    {
        var snapshot = new Dictionary<string, WizardMetricsSnapshot>(StringComparer.Ordinal);
        foreach (var name in Scenarios.Keys)
        {
            snapshot[name] = RunAndMeasure(name);
        }
        File.WriteAllText(BaselinePath(), JsonSerializer.Serialize(snapshot, JsonOpts));
        TestContext.Out.WriteLine($"Baseline written to: {BaselinePath()}");
    }

    private static WizardMetricsSnapshot RunAndMeasure(string scenarioName)
    {
        var ctx = Scenarios[scenarioName]();
        var auctioneer = new SlotAuctioneer(
            new FuzzyBiddingAgent(),
            new Stage0HardConstraintChecker(),
            new Stage1SoftConstraintChecker());
        var outcome = auctioneer.Run(ctx, new Random(Seed));
        return WizardMetricsCalculator.Compute(outcome.Scenario, ctx, outcome.Escalation.Entries.Count);
    }

    private static Dictionary<string, WizardMetricsSnapshot> LoadBaseline()
    {
        var path = BaselinePath();
        if (!File.Exists(path))
        {
            return new Dictionary<string, WizardMetricsSnapshot>(StringComparer.Ordinal);
        }
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, WizardMetricsSnapshot>>(json, JsonOpts)
            ?? new Dictionary<string, WizardMetricsSnapshot>(StringComparer.Ordinal);
    }

    private static void AssertWithinTolerance(string scenario, WizardMetricsSnapshot actual, WizardMetricsSnapshot baseline)
    {
        (actual.CoveragePercent - baseline.CoveragePercent).ShouldBeGreaterThanOrEqualTo(CoverageMinDelta,
            $"{scenario}: Coverage dropped from {baseline.CoveragePercent:P1} to {actual.CoveragePercent:P1}");
        (actual.TargetReachedPercent - baseline.TargetReachedPercent).ShouldBeGreaterThanOrEqualTo(TargetReachedMinDelta,
            $"{scenario}: TargetReached dropped from {baseline.TargetReachedPercent:P1} to {actual.TargetReachedPercent:P1}");
        (actual.SlotGini - baseline.SlotGini).ShouldBeLessThanOrEqualTo(GiniMaxDelta,
            $"{scenario}: SlotGini grew from {baseline.SlotGini:F3} to {actual.SlotGini:F3} (more unfair)");
        (actual.ShiftTypeEntropyAvg - baseline.ShiftTypeEntropyAvg).ShouldBeGreaterThanOrEqualTo(EntropyMinDelta,
            $"{scenario}: Shift-type mix entropy dropped from {baseline.ShiftTypeEntropyAvg:F3} to {actual.ShiftTypeEntropyAvg:F3}");
        (actual.Stage1EscalationCount - baseline.Stage1EscalationCount).ShouldBeLessThanOrEqualTo(EscalationMaxDelta,
            $"{scenario}: Escalations grew from {baseline.Stage1EscalationCount} to {actual.Stage1EscalationCount}");
        (actual.MaxConsecutiveBlockLen - baseline.MaxConsecutiveBlockLen).ShouldBeLessThanOrEqualTo(MaxBlockMaxDelta,
            $"{scenario}: MaxConsecutiveBlockLen grew from {baseline.MaxConsecutiveBlockLen} to {actual.MaxConsecutiveBlockLen}");
    }

    private static string BaselinePath([CallerFilePath] string thisFile = "")
    {
        var dir = Path.GetDirectoryName(thisFile)!;
        return Path.Combine(dir, "wizard-metrics-baseline.json");
    }
}
