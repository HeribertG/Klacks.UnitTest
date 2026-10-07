// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.UnitTest.Autofill.Analysis;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario2;
using Klacks.UnitTest.Autofill.Support;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.ProductionConfig;

/// <summary>
/// Runs scenarios 1, 1b and 2 with the engine configuration production uses (see
/// <see cref="ProductionGaConfig"/>) instead of the suite's 20 x 600 configuration, so the suite's green
/// says something about what production actually computes. This fixture MEASURES: it writes the same
/// artifacts as the suite under a <c>.prodconfig</c> name and prints the rotation, package and hour
/// numbers, but it only fails on what is a hard rule regardless of configuration — every demanded shift
/// staffed and no rest or overlap violation. Explicit because it is a measurement for the baseline report,
/// not a gate.
/// </summary>
[TestFixture]
[Explicit("Measurement of the production GA configuration; run on demand for the baseline report.")]
[Category(ProductionConfigCategory)]
[NonParallelizable]
public sealed class ProductionConfigScenarioTests
{
    private const string ProductionConfigCategory = "AutofillProductionConfig";

    private const string ArtifactNameSuffix = ".prodconfig";

    private const string FirstRunSuffix = ".run1";

    private const string SecondRunSuffix = ".run2";

    private const string FirstRunLabel = "prodconfig-run1";

    private const string SecondRunLabel = "prodconfig-run2";

    private const string Scenario1Key = "scenario1";

    private const string Scenario1bKey = "scenario1b";

    private const string Scenario2Key = "scenario2";

    private const string Scenario1ArtifactName = "Scenario1CleanStart";

    private const string Scenario1bArtifactName = "Scenario1bCalibration";

    private const string Scenario2ArtifactName = "Scenario2";

    private const string ShareFormat = "0.####";

    private const string SeedPrefix = ".seed";

    private static readonly string[] ScenarioKeys = [Scenario1Key, Scenario1bKey, Scenario2Key];

    /// <summary>
    /// Every scenario under every seed of the suite's band, so the production configuration is compared
    /// band against band and not one arbitrary trajectory against another.
    /// </summary>
    private static IEnumerable<TestCaseData> Cases()
        => ScenarioKeys.SelectMany(key => AutofillSeedBand.Seeds.Select(seed => new TestCaseData(key, seed)));

    [TestCaseSource(nameof(Cases))]
    public void RunsWithProductionConfigurationAndStaysLegal(string scenarioKey, int seed)
    {
        var (definition, artifactName) = Build(scenarioKey);
        definition = definition with { Config = ProductionGaConfig.Create(seed) };
        var testName = artifactName + ArtifactNameSuffix + SeedPrefix + seed.ToString(CultureInfo.InvariantCulture);

        var (firstPlan, firstGenerations, firstMs) = RunOnce(definition);
        var (secondPlan, _, secondMs) = RunOnce(definition);

        var difference = PlanFingerprint.FirstDifference(
            PlanFingerprint.ForScenario(firstPlan), PlanFingerprint.ForScenario(secondPlan));
        var determinism = new DeterminismMetrics(difference is null, difference);

        var first = AutofillPlanAnalyzer.Analyze(firstPlan, definition, scenarioKey, testName, FirstRunLabel) with
        {
            Determinism = determinism,
            Runtime = new RuntimeMetrics(firstMs, 0),
        };
        var second = AutofillPlanAnalyzer.Analyze(secondPlan, definition, scenarioKey, testName, SecondRunLabel) with
        {
            Determinism = determinism,
        };

        AutofillArtifactWriter.Write(
            first, PlanMatrixRenderer.Render(firstPlan, definition, $"{scenarioKey} / {testName} / run1"), FirstRunSuffix);
        AutofillArtifactWriter.Write(
            second, PlanMatrixRenderer.Render(secondPlan, definition, $"{scenarioKey} / {testName} / run2"), SecondRunSuffix);

        Report(scenarioKey, first, firstGenerations, firstMs, secondMs, determinism);

        first.Coverage.UnfilledShifts.ShouldBeEmpty(
            $"{scenarioKey} with the production configuration leaves {first.Coverage.UnfilledShifts.Count} demanded "
            + "shift(s) unstaffed.");
        first.Coverage.DoubleBookings.ShouldBeEmpty(
            $"{scenarioKey} with the production configuration books {first.Coverage.DoubleBookings.Count} overlapping pair(s).");
        first.Legality.RestViolations.ShouldBeEmpty(
            $"{scenarioKey} with the production configuration breaks the rest time {first.Legality.RestViolations.Count} time(s).");
    }

    private static (AutofillScenarioDefinition Definition, string ArtifactName) Build(string scenarioKey)
        => scenarioKey switch
        {
            Scenario1Key => (BuildCleanStart(AutofillSpecConstants.GuaranteedHours), Scenario1ArtifactName),
            Scenario1bKey => (BuildCleanStart(AutofillSpecConstants.CalibrationGuaranteedHours), Scenario1bArtifactName),
            Scenario2Key => (Scenario2CarryInFixture.Build(), Scenario2ArtifactName),
            _ => throw new ArgumentOutOfRangeException(nameof(scenarioKey), scenarioKey, "Unknown scenario."),
        };

    private static AutofillScenarioDefinition BuildCleanStart(double guaranteedHours)
        => new AutofillScenarioBuilder()
            .WithPeriod(AutofillSpecConstants.PeriodFrom, AutofillSpecConstants.PeriodUntil)
            .WithEmployees(AutofillSpecConstants.EmployeeCount, guaranteedHours)
            .Build();

    private static (CoreScenario Plan, GenerationRecorder Generations, long ElapsedMs) RunOnce(
        AutofillScenarioDefinition definition)
    {
        var recorder = new GenerationRecorder();
        var stopwatch = Stopwatch.StartNew();
        var plan = TokenEvolutionLoop.Create().Run(definition.Context, definition.Config, recorder);
        stopwatch.Stop();
        return (plan, recorder, stopwatch.ElapsedMilliseconds);
    }

    private static void Report(
        string scenarioKey,
        AutofillMetrics metrics,
        GenerationRecorder generations,
        long firstMs,
        long secondMs,
        DeterminismMetrics determinism)
    {
        var block = metrics.Rotation.BlockCompliance;
        var cyclic = metrics.Rotation.CyclicLastToFirst;
        var spread = metrics.Fairness.SpreadPerType;
        TestContext.Out.WriteLine(
            $"[{scenarioKey} prodconfig seed {metrics.TestName}] runtime run1={firstMs} ms, run2={secondMs} ms, last generation="
            + $"{generations.LastGeneration} of {ProductionGaConfig.Create().MaxGenerations}, "
            + $"early stop={generations.StoppedEarly}, deterministic={determinism.RunsIdentical} "
            + $"({determinism.FirstDifference ?? "identical"})");
        TestContext.Out.WriteLine(
            $"[{scenarioKey} prodconfig] coverage={metrics.Coverage.FilledShifts}/{metrics.Coverage.TotalRequiredShifts}, "
            + $"doubleBookings={metrics.Coverage.DoubleBookings.Count}, restViolations={metrics.Legality.RestViolations.Count}, "
            + $"mixed={metrics.Packages.MixedTypeCount}/{metrics.Packages.Items.Count}, "
            + $"shortShare={Share(AutofillPlanAnalyzer.ShortPackageShare(metrics.Packages, AutofillSpecConstants.ShortPackageMaxLength))}, "
            + $"idealShare={Share(metrics.Packages.IdealShare)}, "
            + $"blockCompliance={Share(block.CompliantRate)} ({block.DescendingCount}/{block.PairCount} falling), "
            + $"cyclicForward={Share(cyclic.ForwardRate)} ({cyclic.ForwardCount}/{cyclic.PairCount}), "
            + $"spread E={spread.Early}/L={spread.Late}/N={spread.Night}, "
            + $"hours={string.Join("/", metrics.Hours.PerEmployee.Select(e => e.PlannedHours.ToString(CultureInfo.InvariantCulture)))}, "
            + $"monotonicityViolations={metrics.Hours.MonotonicityViolations.Count}, "
            + $"overIdeal={AutofillPlanAnalyzer.PackagesLongerThan(metrics.Packages, AutofillSpecConstants.MaxAllowedPackageLength)}, "
            + $"carryInOk={metrics.CarryIn.Count(c => c.Ok)}");
    }

    private static string Share(double value) => value.ToString(ShareFormat, CultureInfo.InvariantCulture);
}
