// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.UnitTest.Autofill.Analysis;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Support;

namespace Klacks.UnitTest.Autofill.Chain;

/// <summary>
/// Measures the plan quality of one autofill scenario after every stage of the production chain Wizard 1 -> Wizard 3
/// (deterministic, protected), and of Wizard 2 (Harmonizer) on the same Wizard 1 plan, which production no longer runs in
/// the chain. Wizard 1 runs once per fixture with exactly the scenario suite's input and
/// configuration, and its plan must be slot for slot the suite's stored run1 plan, otherwise the fixture stops. Wizard 2
/// and Wizard 3 run twice from that plan (<see cref="ChainStageRunner"/>) to prove they are reproducible. All three
/// plans are measured with the suite's <see cref="AutofillPlanAnalyzer"/> on the same definition (previous month and
/// ban list included); artifacts land in Autofill/artifacts/chain.
/// <para>
/// Asserted are only invariants that must never break: no lost coverage, no new double booking, rest or night-to-early
/// violation, no weekly or daily cap exceeded, nothing before the period changed, reproducibility. Package, rotation and
/// fairness values are measured and written to the summary, never asserted.
/// </para>
/// <para>
/// Limit: production applies each stage's result to a scenario in the database between the stages, and the Wizard 1
/// apply partitions the placements through the compliance guardrail, which can drop some. This measurement chains the
/// engines directly and sees no such drop.
/// </para>
/// </summary>
public abstract class AutofillChainQualityTestBase
{
    private const string ArtifactFolder = "chain";
    private const string ArtifactSuffix = ".chain.";
    private const string SummaryFileSuffix = ".chain.summary.txt";
    private const string ProtectionFileSuffix = ".chain.protection.txt";
    private const string ProbeSelfCheckFileSuffix = ".swap-chain-selfcheck.txt";
    private const string Wizard1Label = "w1";
    private const string Wizard2Label = "w2";
    private const string Wizard3Label = ChainStageRunner.ProductionLabel;
    private const string SuiteRunLabel = "run1";
    private const string SuiteRunFileSuffix = ".run1.metrics.json";
    private const string ProblemSeparator = "; ";

    private AutofillScenarioDefinition _definition = null!;
    private ChainRunResult _run = null!;
    private ChainRunResult _repeat = null!;
    private IReadOnlyList<ChainStageMeasurement> _stages = [];
    private string _boundaryBefore = string.Empty;
    private string _boundaryAfter = string.Empty;
    private string _summary = string.Empty;
    private string _summaryPath = string.Empty;

    /// <summary>Artifact base name of the scenario in the chain folder.</summary>
    protected abstract string ChainName { get; }

    /// <summary>Artifact folder of the suite fixture whose run1 Wizard 1 must reproduce.</summary>
    protected abstract string SuiteScenarioName { get; }

    /// <summary>Artifact file name of that suite fixture.</summary>
    protected abstract string SuiteTestName { get; }

    /// <summary>Class name of that suite fixture, for the command that regenerates its artifact.</summary>
    protected abstract string SuiteFixtureName { get; }

    /// <summary>Highest list rank the scenario's specification judges the shift-kind spread over.</summary>
    protected abstract int JudgedRankCount { get; }

    /// <summary>Which shift kinds the scenario's specification judges for the spread.</summary>
    protected virtual string JudgedKindsNote => "the specification judges early, late and night.";

    private ChainStageMeasurement Wizard1 => _stages[0];

    private IEnumerable<ChainStageMeasurement> LaterStages => _stages.Skip(1);

    /// <summary>Builds the scenario exactly as the suite fixture does; throws when the fixture is invalid.</summary>
    protected abstract AutofillScenarioDefinition BuildDefinition();

    [OneTimeSetUp]
    public void RunTheChainOnce()
    {
        _definition = BuildDefinition();
        _boundaryBefore = PlanFingerprint.ForBoundaryWorks(_definition.Context.BoundaryLockedWorks);

        var wizard1 = TokenEvolutionLoop.Create().Run(_definition.Context, _definition.Config);
        var wizard1Metrics = Analyze(wizard1, Wizard1Label);
        EnsureSameAsSuiteRun(wizard1Metrics);

        var works = ChainBitmapInputFactory.WorksOf(wizard1, _definition);
        var hints = ChainSofteningHints.Build(_definition, wizard1);
        _run = ChainStageRunner.Run(_definition, works, hints);
        _repeat = ChainStageRunner.Run(_definition, works, hints);
        _boundaryAfter = PlanFingerprint.ForBoundaryWorks(_definition.Context.BoundaryLockedWorks);

        var wizard2 = ChainPlanConverter.ScenarioOf(_run.Wizard2);
        var wizard3 = ChainPlanConverter.ScenarioOf(_run.Wizard3);
        _stages =
        [
            new ChainStageMeasurement(Wizard1Label, wizard1, wizard1Metrics, 0),
            Measure(wizard1, wizard2, Wizard2Label),
            Measure(wizard1, wizard3, Wizard3Label),
        ];

        foreach (var stage in _stages)
        {
            AutofillArtifactWriter.Write(
                stage.Metrics,
                PlanMatrixRenderer.Render(stage.Plan, _definition, $"{ChainName} / {ArtifactFolder} / {stage.Label}"),
                ArtifactSuffix + stage.Label);
        }

        _summary = ChainSummaryTable.Render(ChainName, _definition, _stages, JudgedRankCount, JudgedKindsNote, _run);
        _summaryPath = Path.Combine(AutofillRepositoryPaths.ArtifactDirectory(ArtifactFolder), ChainName + SummaryFileSuffix);
        File.WriteAllText(_summaryPath, _summary);
        TestContext.Out.WriteLine(_summary);

        WriteProtectionComparison(wizard1);
        WriteProbeSelfCheck(wizard1);
    }

    /// <summary>
    /// Runs the positive control of the swap-chain probe on the Wizard 1 plan and writes its line next to the chain
    /// summary. A measurement aid only: nothing is asserted, and a control that cannot run is written as such.
    /// </summary>
    /// <param name="wizard1">The Wizard 1 plan the control opens slots in</param>
    private void WriteProbeSelfCheck(CoreScenario wizard1)
    {
        var path = Path.Combine(AutofillRepositoryPaths.ArtifactDirectory(ArtifactFolder), ChainName + ProbeSelfCheckFileSuffix);
        try
        {
            File.WriteAllLines(
                path, SwapChainProbeSelfCheck.Run(wizard1, _definition.Context).Select(line => ChainName + ": " + line));
        }
        catch (Exception exception)
        {
            File.WriteAllText(path, $"{ChainName}: the swap-chain probe self-check could not run: {exception}");
        }
    }

    /// <summary>
    /// Runs Wizard 3 once more from the Wizard 2 plan, as the former chain did, and writes the comparison with the
    /// production chain next to the chain summary. It is a measurement only: nothing in it is asserted, and a comparison that cannot be taken is
    /// written as such instead of failing the fixture.
    /// </summary>
    /// <param name="wizard1">The Wizard 1 plan the changed assignments are counted against</param>
    private void WriteProtectionComparison(CoreScenario wizard1)
    {
        var path = Path.Combine(AutofillRepositoryPaths.ArtifactDirectory(ArtifactFolder), ChainName + ProtectionFileSuffix);
        try
        {
            var runs = ChainStageRunner.RunProtectionComparison(
                _definition, ChainBitmapInputFactory.WorksOf(wizard1, _definition), _run);
            var stages = _stages
                .Concat(runs.Skip(1).Select(run => Measure(wizard1, ChainPlanConverter.ScenarioOf(run.Works), run.Label)))
                .ToList();
            File.WriteAllText(
                path,
                ChainSummaryTable.Render(ChainName, _definition, stages, JudgedRankCount, JudgedKindsNote, _run)
                + ChainProtectionSummary.Render(runs));
        }
        catch (Exception exception)
        {
            File.WriteAllText(path, $"{ChainName}: the protection comparison could not be taken: {exception}");
        }
    }

    [Test]
    public void Measurement_IsWrittenAndReported()
    {
        TestContext.Out.WriteLine(_summary);
        TestContext.Out.WriteLine("summary: " + _summaryPath);

        File.Exists(_summaryPath).ShouldBeTrue($"the chain summary must be written to {_summaryPath}");
    }

    [Test]
    public void LaterStages_LoseNoCoverage()
    {
        var wizard1 = UnfilledSeats(Wizard1.Metrics);
        var problems = LaterStages
            .SelectMany(stage => UnfilledSeats(stage.Metrics)
                .Where(seat => seat.Value > wizard1.GetValueOrDefault(seat.Key))
                .Select(seat => $"{stage.Label}: {seat.Key} has {seat.Value} unfilled seat(s), W1 had {wizard1.GetValueOrDefault(seat.Key)}"))
            .ToList();

        problems.ShouldBeEmpty(
            $"Wizard 2 and 3 must keep every slot Wizard 1 staffed (filled: "
            + string.Join(", ", _stages.Select(s => $"{s.Label} {s.Metrics.Coverage.FilledShifts}")) + "). "
            + string.Join(ProblemSeparator, problems));
    }

    [Test]
    public void LaterStages_CreateNoDoubleBooking()
    {
        var problems = NewViolations(m => m.Coverage.DoubleBookings
            .Select(b => $"{b.Employee} {b.Date:yyyy-MM-dd} {b.Reason}"));

        problems.ShouldBeEmpty("Wizard 2 and 3 must not create a double booking: " + string.Join(ProblemSeparator, problems));
    }

    [Test]
    public void LaterStages_CreateNoRestOrNightToEarlyViolation()
    {
        var problems = NewViolations(m => m.Legality.RestViolations
                .Select(v => $"rest {v.Employee} {v.DateFrom:yyyy-MM-dd} {v.FromShift} -> {v.DateTo:yyyy-MM-dd} {v.ToShift}"))
            .Concat(NewViolations(m => m.Legality.NightToEarlyViolations
                .Select(v => $"night-to-early {v.Employee} {v.Date:yyyy-MM-dd}")))
            .ToList();

        problems.ShouldBeEmpty(
            "Wizard 2 and 3 must not create a rest-time or night-to-early violation (previous month included): "
            + string.Join(ProblemSeparator, problems));
    }

    [Test]
    public void LaterStages_KeepTheWeeklyAndDailyCaps()
    {
        var problems = LaterStages
            .SelectMany(stage => ChainPlanFacts.WeeklyCapViolations(stage.Plan, _definition)
                .Concat(ChainPlanFacts.DailyCapViolations(stage.Plan, _definition))
                .Select(v => $"{stage.Label}: {v}"))
            .ToList();
        var wizard1Count = ChainPlanFacts.WeeklyCapViolations(Wizard1.Plan, _definition).Count
            + ChainPlanFacts.DailyCapViolations(Wizard1.Plan, _definition).Count;

        problems.ShouldBeEmpty(
            "Wizard 2 and 3 must keep every weekly cap (ISO week, paid hours on the start day, previous month included) and "
            + $"every daily cap; Wizard 1 holds {wizard1Count} cap violation(s). " + string.Join(ProblemSeparator, problems));
    }

    [Test]
    public void LaterStages_ChangeNothingBeforeThePeriod()
    {
        var problems = new List<string>();
        var boundaryDifference = PlanFingerprint.FirstDifference(_boundaryBefore, _boundaryAfter);
        if (boundaryDifference is not null)
        {
            problems.Add($"the previous-month works changed: {boundaryDifference}");
        }

        problems.AddRange(LaterStages.SelectMany(stage => AutofillPlanAnalyzer.TokensOutsidePeriod(stage.Plan, _definition)
            .Select(t => $"{stage.Label} placed {t.AgentId} on {t.Date:yyyy-MM-dd}, outside the period")));

        problems.ShouldBeEmpty("Wizard 2 and 3 must leave everything before the period untouched: " + string.Join(ProblemSeparator, problems));
    }

    [Test]
    public void Chain_IsReproducible()
    {
        _run.Wizard3Search.StopReason.ShouldNotBe(
            DeterministicSearchStopReason.WallClockBudget, "a Wizard 3 run stopped by the clock is not reproducible");
        (_run.Wizard3Search.RegionRebuild?.WallClockBudgetHit ?? false).ShouldBeFalse(
            "the Wizard 3 region rebuild must not be stopped by the clock");

        Fingerprint(_repeat.Wizard2).ShouldBe(Fingerprint(_run.Wizard2), "Wizard 2 differs between two runs from the same Wizard 1 plan");
        Fingerprint(_repeat.Wizard3).ShouldBe(Fingerprint(_run.Wizard3), "Wizard 3 differs between two runs from the same Wizard 1 plan");
    }

    /// <summary>The clean-start definition of scenario 1 and variant 1b, built exactly like Scenario1AssertionsBase.</summary>
    /// <param name="guaranteedHours">Guaranteed hours of every employee</param>
    protected static AutofillScenarioDefinition BuildCleanStart(double guaranteedHours)
        => new AutofillScenarioBuilder()
            .WithPeriod(AutofillSpecConstants.PeriodFrom, AutofillSpecConstants.PeriodUntil)
            .WithEmployees(AutofillSpecConstants.EmployeeCount, guaranteedHours)
            .WithRandomSeed(AutofillSpecConstants.RandomSeed)
            .WithGaParameters(AutofillSpecConstants.PopulationSize, AutofillSpecConstants.MaxGenerations)
            .Build();

    /// <summary>Returns the definition, or fails when the suite's fixture validation reports a problem.</summary>
    /// <param name="definition">Definition built by the suite's fixture</param>
    /// <param name="problems">Problems the suite's validation reported</param>
    protected static AutofillScenarioDefinition Valid(AutofillScenarioDefinition definition, IReadOnlyList<string> problems)
        => problems.Count == 0
            ? definition
            : throw new InvalidOperationException(
                "The scenario fixture is invalid, so the chain was not run: " + string.Join(ProblemSeparator, problems));

    private AutofillMetrics Analyze(CoreScenario plan, string label)
        => AutofillPlanAnalyzer.Analyze(plan, _definition, ArtifactFolder, ChainName, label);

    private ChainStageMeasurement Measure(CoreScenario wizard1, CoreScenario plan, string label)
        => new(
            label,
            plan,
            Analyze(plan, label),
            AutofillPlanAnalyzer.DiffAssignments(wizard1, _definition, plan, _definition).ChangedCount);

    private void EnsureSameAsSuiteRun(AutofillMetrics wizard1)
    {
        var regenerate = $"Run the suite fixture once and start this one afterwards: dotnet test --filter \"FullyQualifiedName~{SuiteFixtureName}\"";
        var expectedPath = Path.GetFullPath(Path.Combine(
            AutofillRepositoryPaths.ArtifactDirectory(SuiteScenarioName), SuiteTestName + SuiteRunFileSuffix));
        var load = BaselineMetricsReader.Load(SuiteScenarioName, SuiteTestName, SuiteRunLabel);
        if (load.Snapshot is null)
        {
            throw new InvalidOperationException(
                $"{ChainName}: the Wizard 1 plan cannot be proven identical to the suite's run1. {load.Problem} {regenerate}");
        }

        if (!string.Equals(Path.GetFullPath(load.Source), expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{ChainName}: {BaselineMetricsReader.BaselineOverrideVariable} points at '{load.Source}', not at the suite "
                + $"artifact '{expectedPath}'; unset it for the chain measurement.");
        }

        var snapshot = load.Snapshot;
        if (!string.Equals(snapshot.TestName, SuiteTestName, StringComparison.Ordinal)
            || !string.Equals(snapshot.RunLabel, SuiteRunLabel, StringComparison.Ordinal)
            || !snapshot.HasAssignments)
        {
            throw new InvalidOperationException(
                $"{ChainName}: '{expectedPath}' is not a run1 measurement of {SuiteTestName} with an assignment list "
                + $"(test '{snapshot.TestName}', run '{snapshot.RunLabel}', {snapshot.Assignments.Count} assignments). {regenerate}");
        }

        var difference = FirstDifference(snapshot.Assignments, wizard1.Assignments);
        if (difference is not null)
        {
            throw new InvalidOperationException(
                $"{ChainName}: the Wizard 1 plan differs from the suite's run1 ({expectedPath}) at {difference}. Possible causes: "
                + "the artifact is stale (engine changed since it was written), the chain does not build the suite's input, or "
                + "the engine depends on per-process string hashing (the suite proves run1 = run2 only within one process). "
                + regenerate);
        }
    }

    private static string? FirstDifference(IReadOnlyList<PlanAssignmentRow> suite, IReadOnlyList<PlanAssignmentRow> chain)
    {
        var shared = Math.Min(suite.Count, chain.Count);
        for (var i = 0; i < shared; i++)
        {
            if (suite[i] != chain[i])
            {
                return $"assignment {i}: suite {suite[i]} vs chain {chain[i]}";
            }
        }

        return suite.Count == chain.Count ? null : $"assignment count: suite {suite.Count} vs chain {chain.Count}";
    }

    private static Dictionary<string, int> UnfilledSeats(AutofillMetrics metrics)
        => metrics.Coverage.UnfilledShifts
            .GroupBy(u => $"{u.Date:yyyy-MM-dd} {u.ShiftType} order {u.Order}")
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    private List<string> NewViolations(Func<AutofillMetrics, IEnumerable<string>> keysOf)
    {
        var wizard1 = keysOf(Wizard1.Metrics).ToHashSet(StringComparer.Ordinal);
        return LaterStages
            .SelectMany(stage => keysOf(stage.Metrics)
                .Where(key => !wizard1.Contains(key))
                .Select(key => $"{stage.Label}: {key}"))
            .ToList();
    }

    private static string Fingerprint(IReadOnlyList<ChainWork> works)
        => PlanFingerprint.ForScenario(ChainPlanConverter.ScenarioOf(works));
}
