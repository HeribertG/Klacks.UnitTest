// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Analysis;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Support;
using NUnit.Framework;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario6;

/// <summary>
/// Shared plumbing of the scenario-6 runs: assemble the fixture, guard it, compute and log the
/// availability arithmetic BEFORE the engine starts, run it twice through the deterministic runner,
/// load the stored control run and expose everything to the assertions. A fixture that does not pass
/// its guard makes every assertion inconclusive instead of red, so a setup mistake can never be read
/// as a verdict about the algorithm.
/// <para>
/// The control run is LOADED, not executed. S6-K is the finished scenario-5 main run; re-running it
/// would cost another 522 s per scenario-6 variant. A control that could not be loaded leaves the
/// comparison in the NotAvailable state, and the assertions built on it go inconclusive — never green.
/// </para>
/// </summary>
public abstract class Scenario6RunTestBase : Scenario6AssertionsBase
{
    /// <summary>Artifact suffix of the first of the two identical runs; mirrors the deterministic runner.</summary>
    private const string FirstRunSuffix = ".run1";

    private AutofillScenarioDefinition? _definition;

    private DeterministicRunResult? _run;

    private BaselineDiff _diff = BaselineDiff.None;

    private IReadOnlyList<string> _fixtureProblems = [];

    /// <inheritdoc />
    protected override AutofillScenarioDefinition Definition
    {
        get
        {
            EnsureFixtureIsValid();
            return _definition!;
        }
    }

    /// <inheritdoc />
    protected override DeterministicRunResult Run
    {
        get
        {
            EnsureFixtureIsValid();
            return _run!;
        }
    }

    /// <summary>The comparison against the stored control run S6-K; NotAvailable when it could not be read.</summary>
    protected BaselineDiff DiffVsBaseline => _diff;

    /// <summary>False once a fixture problem was recorded.</summary>
    protected bool FixtureIsValid => _fixtureProblems.Count == 0;

    /// <summary>
    /// Assembles, guards and runs one scenario-6 variant, then loads the stored control run and writes
    /// the diagnosis block every run contributes to the report.
    /// </summary>
    /// <param name="build">Builds the scenario; exceptions become fixture problems</param>
    /// <param name="artifactName">Literal artifact file name — never a generated test name</param>
    /// <param name="extraChecks">Further guard checks this variant requires; empty for most runs</param>
    protected void BuildGuardAndRun(
        Func<AutofillScenarioDefinition> build,
        string artifactName,
        Func<AutofillScenarioDefinition, IReadOnlyList<string>>? extraChecks = null)
    {
        try
        {
            _definition = build();
        }
        catch (Exception exception)
        {
            _fixtureProblems = [$"{artifactName} could not be assembled: {exception.Message}"];
            return;
        }

        var problems = new List<string>(Scenario6FixtureGuard.Validate(_definition));
        if (extraChecks is not null)
        {
            problems.AddRange(extraChecks(_definition));
        }

        _fixtureProblems = problems;
        if (_fixtureProblems.Count > 0)
        {
            return;
        }

        foreach (var note in Scenario6FixtureGuard.Notes(_definition))
        {
            TestContext.Out.WriteLine("fixture note: " + note);
        }

        WriteFramingBeforeTheRun(artifactName, _definition);

        _run = DeterministicRunner.Run(
            _definition,
            Scenario6SpecConstants.ScenarioName,
            artifactName,
            Scenario6GaParameters.MeasuredReferenceRunMs);

        _diff = BaselineDiffAnalyzer.Build(
            BaselineMetricsReader.Load(
                Scenario6SpecConstants.BaselineScenarioName,
                Scenario6SpecConstants.BaselineTestName,
                Scenario6SpecConstants.BaselineRunLabel),
            _run.Metrics,
            _definition,
            Scenario6SpecConstants.BaselineLabel);

        RewriteFirstRunArtifactWithTheDiff(_definition, _run, _diff, artifactName);
        WriteDiagnosis(artifactName, _definition, _run, _diff);
    }

    /// <summary>
    /// Writes the first run's metric artifact a second time, now carrying the comparison against the
    /// control run. The deterministic runner writes its artifacts before the comparison exists — it
    /// knows one scenario and one plan — and the specification asks for <c>diffVsS5a.*</c> to stand in
    /// the artifact rather than only in the console output, so the file is completed here.
    /// </summary>
    /// <param name="definition">Scenario that was run</param>
    /// <param name="run">Result of the deterministic runner</param>
    /// <param name="diff">Comparison against the stored control run</param>
    /// <param name="artifactName">Artifact file name of this run</param>
    private static void RewriteFirstRunArtifactWithTheDiff(
        AutofillScenarioDefinition definition,
        DeterministicRunResult run,
        BaselineDiff diff,
        string artifactName)
    {
        var matrix = PlanMatrixRenderer.Render(
            run.Plan, definition, $"{Scenario6SpecConstants.ScenarioName} / {artifactName} / run1");
        AutofillArtifactWriter.Write(run.Metrics with { DiffVsBaseline = diff }, matrix, FirstRunSuffix);
    }

    /// <summary>Records a fixture problem that keeps every assertion from being judged.</summary>
    /// <param name="problems">Problem messages</param>
    protected void RecordFixtureProblems(IReadOnlyList<string> problems) => _fixtureProblems = problems;

    /// <summary>
    /// Makes an assertion inconclusive when the stored control run could not be read. It is a separate
    /// call and not a silent skip on purpose: without the control there is nothing to compare, and a
    /// comparison of nothing must never be reported as agreement.
    /// </summary>
    protected void EnsureBaselineIsAvailable()
    {
        if (!_diff.IsAvailable)
        {
            Assert.Inconclusive(
                "The control run S6-K could not be read, so nothing was compared. This is NOT 'no difference'. "
                + string.Join(" | ", _diff.Notes)
                + $" Candidate paths: {_diff.BaselineSource}. Run scenario 5a once, or point the environment "
                + $"variable {BaselineMetricsReader.BaselineOverrideVariable} at a secured copy of its metrics JSON.");
        }
    }

    /// <summary>
    /// The arithmetic the specification requires the test to compute and protocol BEFORE the run.
    /// Every one of these numbers is a property of the FIXTURE; computing them after the engine has
    /// spoken would invite reading them as a result.
    /// </summary>
    /// <param name="artifactName">Artifact file name of this run</param>
    /// <param name="definition">Scenario about to be run</param>
    protected static void WriteFramingBeforeTheRun(string artifactName, AutofillScenarioDefinition definition)
    {
        var availability = AbsenceAnalyzer.BuildAvailability(definition);

        TestContext.Out.WriteLine(
            $"{artifactName} availability BEFORE the run — "
            + Scenario6Diagnostics.DescribeAvailability(availability));

        TestContext.Out.WriteLine(
            $"{artifactName} absences BEFORE the run — "
            + $"{definition.Absences.Rows.Count.ToString(CultureInfo.InvariantCulture)} one-day rows over "
            + $"{definition.Absences.AllWindows().Count.ToString(CultureInfo.InvariantCulture)} window(s); credit per "
            + "employee "
            + string.Join(
                ", ",
                definition.Absences.Employees().Select(e =>
                    $"{e} {((double)definition.Absences.CreditHoursOf(e)).ToString("0.##", CultureInfo.InvariantCulture)} h")));

        TestContext.Out.WriteLine(
            $"{artifactName} note: the control run S6-K is the finished scenario-5 main run and is NOT executed "
            + "again. Its measurement is read from the artifact; a missing artifact makes the comparison "
            + "inconclusive rather than empty.");
    }

    /// <summary>
    /// Writes the framing every scenario-6 report needs next to the numbers.
    /// </summary>
    /// <param name="artifactName">Artifact file name of this run</param>
    /// <param name="definition">Scenario that was run</param>
    /// <param name="run">Result of the deterministic runner</param>
    /// <param name="diff">Comparison against the stored control run</param>
    protected static void WriteDiagnosis(
        string artifactName,
        AutofillScenarioDefinition definition,
        DeterministicRunResult run,
        BaselineDiff diff)
    {
        TestContext.Out.WriteLine(
            $"{artifactName}: seed {definition.Config.RandomSeed.ToString(CultureInfo.InvariantCulture)}, "
            + $"{definition.Context.Shifts.Count.ToString(CultureInfo.InvariantCulture)} demanded shifts, "
            + $"{definition.Context.Agents.Count.ToString(CultureInfo.InvariantCulture)} employees, "
            + $"{definition.Context.BreakBlockers.Count.ToString(CultureInfo.InvariantCulture)} absence blockers, "
            + $"{definition.Context.BoundaryLockedWorks.Count.ToString(CultureInfo.InvariantCulture)} carry-in days, "
            + $"{definition.Context.LockedWorks.Count.ToString(CultureInfo.InvariantCulture)} locked works, "
            + $"population {definition.Config.PopulationSize.ToString(CultureInfo.InvariantCulture)}, generations "
            + definition.Config.MaxGenerations.ToString(CultureInfo.InvariantCulture));

        TestContext.Out.WriteLine(
            "measured absence windows: " + Scenario6Diagnostics.DescribeWindows(run.Metrics.Absences.Entries));
        TestContext.Out.WriteLine(
            "measured absence violations: "
            + Scenario6Diagnostics.DescribeViolations(run.Metrics.Absences.Violations));
        TestContext.Out.WriteLine(
            "measured boundary cases: "
            + Scenario6Diagnostics.DescribeBoundaryCases(run.Metrics.Absences.BoundaryCases));
        TestContext.Out.WriteLine(
            "measured credit target: "
            + Scenario6Diagnostics.DescribeCreditTarget(run.Metrics.Hours.CreditTarget));
        TestContext.Out.WriteLine(
            "measured absence cuts: " + Scenario6Diagnostics.DescribeCuts(run.Metrics.Packages.AbsenceCuts));
        TestContext.Out.WriteLine(
            "measured continuity across the absence: "
            + Scenario6Diagnostics.DescribeContinuity(run.Metrics.Rotation.ContinuityAcrossAbsence));
        TestContext.Out.WriteLine("diff against the control run: " + Scenario6Diagnostics.DescribeDiff(diff));
        TestContext.Out.WriteLine(
            "hour diff against the control run: " + Scenario6Diagnostics.DescribeHoursDiff(diff.HoursDiffs));

        foreach (var path in run.ArtifactPaths)
        {
            TestContext.Out.WriteLine("artifact: " + path);
        }
    }

    private void EnsureFixtureIsValid()
    {
        if (_fixtureProblems.Count > 0)
        {
            Assert.Inconclusive(
                "fixture invalid — the scenario was not run, so no rule assertion can be judged: "
                + string.Join(ProblemSeparator, _fixtureProblems));
        }
    }
}
