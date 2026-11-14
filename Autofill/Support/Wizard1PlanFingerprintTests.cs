// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario2;
using Klacks.UnitTest.Autofill.Scenarios.Scenario3;
using Klacks.UnitTest.Autofill.Scenarios.Scenario4;
using Klacks.UnitTest.Autofill.Scenarios.Scenario5;
using NUnit.Framework;

namespace Klacks.UnitTest.Autofill.Support;

/// <summary>
/// Proof tool for changes that must not move a Wizard 1 result, such as a pure speedup: runs the production entry
/// <c>TokenEvolutionLoop.Create().Run</c> (first run, wheel run where it applies, judge) without a time budget per
/// scenario and seed and writes the end plan as text — every assignment with its surcharges plus the final stage
/// fitness — to Autofill/artifacts/wizard1-fingerprint. Run it once on the code before the change, copy the folder,
/// point <see cref="BaselineVariable"/> at the copy and run it again on the changed code: every plan must then be
/// identical to the byte. Without the variable the test only records. The wall time and the generation trace lines are
/// printed for runtime comparisons only; they never enter the recorded plan. The large scenarios and the 50-employee
/// scale case run with a generation cap so the proof stays affordable; the cap is part of the case name.
/// </summary>
[TestFixture]
[NonParallelizable]
[Explicit("Proof tool: records or compares the Wizard 1 end plans of several scenarios and seeds; runs for many minutes")]
public sealed class Wizard1PlanFingerprintTests
{
    public const string BaselineVariable = "KLACKS_W1_FINGERPRINT_BASELINE";

    private const string ArtifactFolder = "wizard1-fingerprint";
    private const string FileSuffix = ".plan.txt";
    private const string StageFormat = "R";
    private const string FieldSeparator = "|";
    private const string GenerationTraceMarker = " gen=";
    private const int LargeGenerationCap = 30;
    private const int ScaleGenerations = 3;
    private static readonly int[] Seeds = [42, 7, 1234];
    private static readonly int[] ScaleSeeds = [42, 7];
    private static readonly DateOnly MarchFrom = new(2026, 3, 1);
    private static readonly DateOnly MarchUntil = new(2026, 3, 31);

    private static readonly Dictionary<string, (Func<AutofillScenarioDefinition> Build, int[] Seeds)> Scenarios = new()
    {
        ["S1-2026-03"] = (() => CleanStart(MarchFrom, MarchUntil), Seeds),
        ["S2"] = (Scenario2CarryInFixture.Build, Seeds),
        ["S3-L1"] = (Scenario3EligibilityFixture.BuildL1, Seeds),
        [$"S4a-gen{LargeGenerationCap}"] = (() => Capped(Scenario4CarryInFixture.BuildMainRun()), Seeds),
        [$"S5a-gen{LargeGenerationCap}"] = (() => Capped(Scenario5Fixture.BuildMainRun()), Seeds),
        [$"Scale{AutofillScaleFixture.EmployeeCount}-gen{ScaleGenerations}"] =
            (() => AutofillScaleFixture.Build(AutofillSpecConstants.PopulationSize, ScaleGenerations), ScaleSeeds),
    };

    private static IEnumerable<TestCaseData> Cases()
        => Scenarios.SelectMany(entry => entry.Value.Seeds
            .Select(seed => new TestCaseData(entry.Key, seed).SetName($"{entry.Key}_seed{seed}")));

    [TestCaseSource(nameof(Cases))]
    public void EndPlanMatchesTheBaseline(string scenario, int seed)
    {
        var built = Scenarios[scenario].Build();
        var definition = built with { Config = built.Config with { RandomSeed = seed, MaxRuntime = null } };

        var generations = new List<string>();
        var clock = Stopwatch.StartNew();
        var plan = TokenEvolutionLoop.Create().Run(
            definition.Context,
            definition.Config,
            trace: line =>
            {
                if (line.Contains(GenerationTraceMarker, StringComparison.Ordinal))
                {
                    generations.Add(line);
                }
            });
        clock.Stop();

        var fileName = $"{scenario}.seed{seed}{FileSuffix}";
        var text = Render(plan);
        File.WriteAllText(Path.Combine(AutofillRepositoryPaths.ArtifactDirectory(ArtifactFolder), fileName), text);
        TestContext.Out.WriteLine(
            $"{scenario} seed {seed}: {plan.Tokens.Count} tokens, Wizard 1 took {clock.Elapsed.TotalSeconds:0.0} s");
        foreach (var line in generations)
        {
            TestContext.Out.WriteLine($"  {line}");
        }

        var baselineFolder = Environment.GetEnvironmentVariable(BaselineVariable);
        if (string.IsNullOrEmpty(baselineFolder))
        {
            return;
        }

        var baselinePath = Path.Combine(baselineFolder, fileName);
        File.Exists(baselinePath).ShouldBeTrue($"no baseline plan at {baselinePath}");
        var difference = PlanFingerprint.FirstDifference(File.ReadAllText(baselinePath), text);
        difference.ShouldBeNull($"{scenario} seed {seed}: the end plan differs from the baseline: {difference}");
    }

    private static string Render(CoreScenario plan)
    {
        var surcharges = plan.Tokens
            .Select(t => string.Join(
                FieldSeparator,
                t.AgentId,
                t.Date.ToString(AutofillSpecConstants.IsoDateFormat, CultureInfo.InvariantCulture),
                t.ShiftRefId.ToString(),
                t.Surcharges.ToString(CultureInfo.InvariantCulture)))
            .OrderBy(line => line, StringComparer.Ordinal);

        var text = new StringBuilder();
        text.AppendLine(PlanFingerprint.ForScenario(plan));
        text.AppendLine("surcharges:");
        foreach (var line in surcharges)
        {
            text.AppendLine(line);
        }

        text.AppendLine(string.Join(
            " ",
            $"legality={plan.FitnessStage0Legality.ToString(CultureInfo.InvariantCulture)}",
            $"stage0={plan.FitnessStage0.ToString(CultureInfo.InvariantCulture)}",
            $"stage1={plan.FitnessStage1.ToString(StageFormat, CultureInfo.InvariantCulture)}",
            $"stage2={plan.FitnessStage2.ToString(StageFormat, CultureInfo.InvariantCulture)}",
            $"stage3={plan.FitnessStage3.ToString(StageFormat, CultureInfo.InvariantCulture)}",
            $"stage4={plan.FitnessStage4.ToString(StageFormat, CultureInfo.InvariantCulture)}"));
        return text.ToString();
    }

    private static AutofillScenarioDefinition CleanStart(DateOnly from, DateOnly until)
        => new AutofillScenarioBuilder()
            .WithPeriod(from, until)
            .WithEmployees(AutofillSpecConstants.EmployeeCount, AutofillSpecConstants.GuaranteedHours)
            .WithRandomSeed(AutofillSpecConstants.RandomSeed)
            .WithGaParameters(AutofillSpecConstants.PopulationSize, AutofillSpecConstants.MaxGenerations)
            .Build();

    private static AutofillScenarioDefinition Capped(AutofillScenarioDefinition definition)
        => definition with
        {
            Config = definition.Config with
            {
                MaxGenerations = LargeGenerationCap,
                EarlyStopNoImprovementGenerations = LargeGenerationCap,
            },
        };
}
