// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Support;
using NUnit.Framework;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario4;

/// <summary>
/// Shared plumbing of the five scenario-4 runs: assemble the fixture, guard it, run it twice through
/// the deterministic runner, and expose the cached result to the assertions. A fixture that does not
/// pass its guard makes every assertion inconclusive instead of red, so a setup mistake can never be
/// read as a verdict about the algorithm.
/// </summary>
public abstract class Scenario4RunTestBase : Scenario4AssertionsBase
{
    private AutofillScenarioDefinition? _definition;

    private DeterministicRunResult? _run;

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

    /// <summary>
    /// Assembles, guards and runs one scenario-4 variant, then writes the diagnosis block every run
    /// contributes to the report.
    /// </summary>
    /// <param name="build">Builds the scenario; exceptions become fixture problems</param>
    /// <param name="artifactName">Literal artifact file name — never a generated test name</param>
    /// <param name="runsGuard">False for the variant without a previous month, whose tables do not exist</param>
    protected void BuildGuardAndRun(
        Func<AutofillScenarioDefinition> build, string artifactName, bool runsGuard = true)
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

        if (runsGuard)
        {
            _fixtureProblems = Scenario4FixtureGuard.Validate(_definition);
            if (_fixtureProblems.Count > 0)
            {
                return;
            }

            foreach (var note in Scenario4FixtureGuard.Notes(_definition))
            {
                TestContext.Out.WriteLine("fixture note: " + note);
            }
        }

        _run = DeterministicRunner.Run(
            _definition,
            Scenario4SpecConstants.ScenarioName,
            artifactName,
            Scenario4GaParameters.MeasuredReferenceRunMs);
        WriteDiagnosis(artifactName, _definition, _run);
    }

    /// <summary>Registers a scenario that was already assembled and run elsewhere, for example a replanning run.</summary>
    /// <param name="definition">Assembled scenario</param>
    /// <param name="run">Result of the deterministic runner over it</param>
    protected void UseExistingRun(AutofillScenarioDefinition definition, DeterministicRunResult run)
    {
        _definition = definition;
        _run = run;
    }

    /// <summary>Records a fixture problem that keeps every assertion from being judged.</summary>
    /// <param name="problems">Problem messages</param>
    protected void RecordFixtureProblems(IReadOnlyList<string> problems) => _fixtureProblems = problems;

    /// <summary>
    /// Writes the framing every scenario-4 report needs next to the numbers: the two arithmetic
    /// resolutions the scenario allows, the slot order the pinned ids produce, and the artifact paths.
    /// </summary>
    /// <param name="artifactName">Artifact file name of this run</param>
    /// <param name="definition">Scenario that was run</param>
    /// <param name="run">Result of the deterministic runner</param>
    protected static void WriteDiagnosis(
        string artifactName, AutofillScenarioDefinition definition, DeterministicRunResult run)
    {
        TestContext.Out.WriteLine(
            $"{artifactName}: seed {definition.Config.RandomSeed.ToString(CultureInfo.InvariantCulture)}, "
            + $"{definition.Context.Shifts.Count.ToString(CultureInfo.InvariantCulture)} demanded shifts, "
            + $"{definition.Context.Agents.Count.ToString(CultureInfo.InvariantCulture)} employees, "
            + $"{definition.Context.BoundaryLockedWorks.Count.ToString(CultureInfo.InvariantCulture)} carry-in days, "
            + $"{definition.Context.LockedWorks.Count.ToString(CultureInfo.InvariantCulture)} locked works, "
            + $"population {definition.Config.PopulationSize.ToString(CultureInfo.InvariantCulture)}, generations "
            + $"{definition.Config.MaxGenerations.ToString(CultureInfo.InvariantCulture)}");

        TestContext.Out.WriteLine(
            "auction order inside one day (identical times, so the pinned ids decide): "
            + AutofillShiftCatalog.DescribeAuctionOrderOfOneDay(definition.OrderCount));

        TestContext.Out.WriteLine(
            "rhythm resolution reference: work share 60 % implies a cycle of 8.33 days, so packages of "
            + $"{Scenario4SpecConstants.RhythmPackageLength.ToString(CultureInfo.InvariantCulture)} days and free "
            + $"blocks of {Scenario4SpecConstants.RhythmFreeBlockLow.ToString(CultureInfo.InvariantCulture)} to "
            + $"{Scenario4SpecConstants.RhythmFreeBlockHigh.ToString(CultureInfo.InvariantCulture)} days");
        TestContext.Out.WriteLine(
            "top-down resolution reference: ranks 1 to "
            + $"{Scenario4SpecConstants.TopRankUpperBound.ToString(CultureInfo.InvariantCulture)} at "
            + $"{Scenario4SpecConstants.ReferenceShiftsTopRanks.ToString(CultureInfo.InvariantCulture)} shifts, rank "
            + $"{Scenario4SpecConstants.RemainderRank.ToString(CultureInfo.InvariantCulture)} at "
            + $"{Scenario4SpecConstants.ReferenceShiftsRemainderRank.ToString(CultureInfo.InvariantCulture)}, the last "
            + $"{Scenario4SpecConstants.ReferenceZeroHourRanks.ToString(CultureInfo.InvariantCulture)} ranks at zero");

        TestContext.Out.WriteLine(
            "measured coverage per order: "
            + Scenario4Diagnostics.DescribeOrderCoverage(run.Metrics.Coverage.PerOrder));
        TestContext.Out.WriteLine(
            "measured hours: " + Scenario4Diagnostics.DescribeHours(run.Metrics.Hours.PerEmployee));
        TestContext.Out.WriteLine(
            "measured free blocks: "
            + Scenario4Diagnostics.DescribeFreeBlocks(run.Metrics.Packages.FreeBlockHistogram));
        TestContext.Out.WriteLine(
            "measured order distribution: "
            + Scenario4Diagnostics.DescribeOrderDistribution(run.Metrics.Orders.EmployeeDistribution));
        TestContext.Out.WriteLine(
            "measured forced leading free days: "
            + Scenario4Diagnostics.DescribeForcedFreeDays(run.Metrics.Packages.ForcedExtraFreeDays));

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
