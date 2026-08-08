// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.UnitTest.Autofill.Analysis;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Support;

/// <summary>
/// Runs one scenario twice with the same input and the same seed, measures both plans, writes the
/// artifacts of both and answers whether the two runs are the same plan. A run that differs from
/// itself makes every other assertion meaningless, so this is the first thing a scenario test checks.
/// <para>
/// Determinism rests on three settings the builder pins and this runner never overrides: a fixed
/// RandomSeed, sequential evaluation, and no wall-clock budget — a time budget would cut the run off
/// at a different generation on a different machine. It also snapshots the fixed previous-month works
/// before the run and compares them afterwards, which is the engine-level form of "nothing before the
/// period start was changed".
/// </para>
/// </summary>
public static class DeterministicRunner
{
    private const string FirstRunSuffix = ".run1";
    private const string SecondRunSuffix = ".run2";
    private const string SeedRunSuffix = ".seed";

    private const string FirstRunLabel = "run1";
    private const string SecondRunLabel = "run2";
    private const string SeedRunLabel = "auction-seed";

    /// <summary>
    /// Executes both runs plus the auction seed diagnosis and writes all artifacts.
    /// </summary>
    /// <param name="definition">Scenario to run</param>
    /// <param name="scenarioName">Scenario name; becomes the artifact folder</param>
    /// <param name="testName">Test name; becomes the artifact file name</param>
    public static DeterministicRunResult Run(
        AutofillScenarioDefinition definition, string scenarioName, string testName)
    {
        var boundaryBefore = PlanFingerprint.ForBoundaryWorks(definition.Context.BoundaryLockedWorks);

        var firstPlan = TokenEvolutionLoop.Create().Run(definition.Context, definition.Config);
        var secondPlan = TokenEvolutionLoop.Create().Run(definition.Context, definition.Config);
        var seedPlan = AutofillSeedPlanFactory.BuildAuctionSeedPlan(definition);

        var difference = PlanFingerprint.FirstDifference(
            PlanFingerprint.ForScenario(firstPlan),
            PlanFingerprint.ForScenario(secondPlan));
        var runsIdentical = difference is null;

        var carryInDifference = PlanFingerprint.FirstDifference(
            boundaryBefore,
            PlanFingerprint.ForBoundaryWorks(definition.Context.BoundaryLockedWorks));

        var determinism = new DeterminismMetrics(runsIdentical, difference);

        var firstMetrics = Measure(firstPlan, definition, scenarioName, testName, FirstRunLabel, determinism);
        var secondMetrics = Measure(secondPlan, definition, scenarioName, testName, SecondRunLabel, determinism);
        var seedMetrics = Measure(seedPlan, definition, scenarioName, testName, SeedRunLabel, determinism);

        var artifacts = new List<string>();
        artifacts.AddRange(AutofillArtifactWriter.Write(
            firstMetrics, PlanMatrixRenderer.Render(firstPlan, definition, Title(scenarioName, testName, FirstRunLabel)), FirstRunSuffix));
        artifacts.AddRange(AutofillArtifactWriter.Write(
            secondMetrics, PlanMatrixRenderer.Render(secondPlan, definition, Title(scenarioName, testName, SecondRunLabel)), SecondRunSuffix));
        artifacts.AddRange(AutofillArtifactWriter.Write(
            seedMetrics, PlanMatrixRenderer.Render(seedPlan, definition, Title(scenarioName, testName, SeedRunLabel)), SeedRunSuffix));

        return new DeterministicRunResult(
            Plan: firstPlan,
            Metrics: firstMetrics,
            SecondPlan: secondPlan,
            SecondMetrics: secondMetrics,
            SeedPlan: seedPlan,
            SeedMetrics: seedMetrics,
            RunsIdentical: runsIdentical,
            FirstDifference: difference,
            CarryInUnchanged: carryInDifference is null,
            CarryInDifference: carryInDifference,
            ArtifactPaths: artifacts);
    }

    private static AutofillMetrics Measure(
        Klacks.ScheduleOptimizer.Models.CoreScenario plan,
        AutofillScenarioDefinition definition,
        string scenarioName,
        string testName,
        string runLabel,
        DeterminismMetrics determinism)
        => AutofillPlanAnalyzer.Analyze(plan, definition, scenarioName, testName, runLabel) with
        {
            Determinism = determinism,
        };

    private static string Title(string scenarioName, string testName, string runLabel)
        => $"{scenarioName} / {testName} / {runLabel}";
}
