// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Support;
using NUnit.Framework;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario5;

/// <summary>
/// Shared plumbing of the scenario-5 runs: assemble the fixture, guard it, compute and log the
/// arithmetic framing BEFORE the engine starts, run it twice through the deterministic runner and
/// expose the cached result to the assertions. A fixture that does not pass its guard makes every
/// assertion inconclusive instead of red, so a setup mistake can never be read as a verdict about the
/// algorithm.
/// </summary>
public abstract class Scenario5RunTestBase : Scenario5AssertionsBase
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
    /// Assembles, guards and runs one scenario-5 variant, then writes the diagnosis block every run
    /// contributes to the report.
    /// </summary>
    /// <param name="build">Builds the scenario; exceptions become fixture problems</param>
    /// <param name="artifactName">Literal artifact file name — never a generated test name</param>
    protected void BuildGuardAndRun(Func<AutofillScenarioDefinition> build, string artifactName)
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

        _fixtureProblems = Scenario5FixtureGuard.Validate(_definition);
        if (_fixtureProblems.Count > 0)
        {
            return;
        }

        foreach (var note in Scenario5FixtureGuard.Notes(_definition))
        {
            TestContext.Out.WriteLine("fixture note: " + note);
        }

        WriteFramingBeforeTheRun(artifactName, _definition);

        _run = DeterministicRunner.Run(
            _definition,
            Scenario5SpecConstants.ScenarioName,
            artifactName,
            Scenario5GaParameters.MeasuredReferenceRunMs);
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
    /// False once a fixture problem was recorded. A run that needs a SECOND scenario — the control-group
    /// comparison does — has to ask before building it, because stacking a second run on top of an
    /// already broken first one only buries the original message.
    /// </summary>
    protected bool FixtureIsValid => _fixtureProblems.Count == 0;

    /// <summary>
    /// The arithmetic the specification requires the test to compute and protocol BEFORE the run. It
    /// is written first on purpose: every one of these numbers is a property of the FIXTURE, and
    /// computing them after the engine has spoken would invite reading them as a result.
    /// <para>
    /// The FREE-adjusted ceilings are the part that earns its place. Both employees carrying a FREE
    /// window lose three calendar days each, and they are ranks 1 and 2 — the very ranks the top-down
    /// rule expects to be served first. A gap between them and rank 3 is therefore expected from the
    /// fixture alone, and this block is what lets the phase-C reader tell that cause apart from a
    /// genuine top-down defect instead of guessing.
    /// </para>
    /// </summary>
    /// <param name="artifactName">Artifact file name of this run</param>
    /// <param name="definition">Scenario about to be run</param>
    protected static void WriteFramingBeforeTheRun(string artifactName, AutofillScenarioDefinition definition)
    {
        var employeeDays = definition.EmployeesInListOrder.Count * AutofillSpecConstants.PeriodDays;
        TestContext.Out.WriteLine(
            $"{artifactName} framing BEFORE the run — places "
            + $"{employeeDays.ToString(CultureInfo.InvariantCulture)}, demand "
            + $"{definition.Context.Shifts.Count.ToString(CultureInfo.InvariantCulture)}, work share "
            + (Scenario5SpecConstants.RequiredWorkShare * 100).ToString("0.0", CultureInfo.InvariantCulture)
            + " % against the 5/2 ideal of "
            + (Scenario5SpecConstants.IdealFiveTwoWorkShare * 100).ToString("0.00", CultureInfo.InvariantCulture)
            + " %, so 5/2 is just reachable and the free-block mode is expected at "
            + Scenario5SpecConstants.ExpectedFreeBlockMode.ToString(CultureInfo.InvariantCulture));

        TestContext.Out.WriteLine(
            $"{artifactName} hours — offered "
            + $"{Scenario5SpecConstants.OfferedHours.ToString("0", CultureInfo.InvariantCulture)} h, demanded "
            + $"{Scenario5SpecConstants.TotalRequiredHours.ToString("0", CultureInfo.InvariantCulture)} h = "
            + (Scenario5SpecConstants.TotalRequiredHours / Scenario5SpecConstants.OfferedHours * 100)
                .ToString("0.00", CultureInfo.InvariantCulture)
            + " %");

        TestContext.Out.WriteLine(
            $"{artifactName} top-down reference (a) — 180 h are 22.5 shifts, which nothing can hand out, so a full "
            + $"guarantee costs {Scenario5SpecConstants.FullGuaranteeShifts.ToString(CultureInfo.InvariantCulture)} "
            + $"shifts = {Scenario5SpecConstants.FullGuaranteeHours.ToString("0", CultureInfo.InvariantCulture)} h; "
            + $"{Scenario5SpecConstants.FullGuaranteeRanks.ToString(CultureInfo.InvariantCulture)} ranks reach it and "
            + $"rank {Scenario5SpecConstants.EmployeeCount.ToString(CultureInfo.InvariantCulture)} keeps the remaining "
            + $"{Scenario5SpecConstants.RemainderRankShifts.ToString(CultureInfo.InvariantCulture)} shifts");

        TestContext.Out.WriteLine(
            $"{artifactName} top-down reference (b) — flat share "
            + (Scenario5SpecConstants.TotalRequiredShifts / (double)Scenario5SpecConstants.EmployeeCount)
                .ToString("0.0", CultureInfo.InvariantCulture)
            + $" shifts = {Scenario5SpecConstants.FlatShareHours.ToString("0.0", CultureInfo.InvariantCulture)} h for "
            + "every rank. The test MEASURES which of the two the algorithm picks; neither is asserted");

        TestContext.Out.WriteLine(
            $"{artifactName} night pool — "
            + $"{Scenario5SpecConstants.NightPoolSize.ToString(CultureInfo.InvariantCulture)} employees for "
            + $"{Scenario5SpecConstants.TotalNightShifts.ToString(CultureInfo.InvariantCulture)} night shifts = "
            + (Scenario5SpecConstants.TotalNightShifts / (double)Scenario5SpecConstants.NightPoolSize)
                .ToString("0.0", CultureInfo.InvariantCulture)
            + " per head");

        TestContext.Out.WriteLine(
            $"{artifactName} day shifts — "
            + $"{Scenario5SpecConstants.TotalDayShifts.ToString(CultureInfo.InvariantCulture)} exist and the two "
            + "preferring employees can work about "
            + $"{Scenario5SpecConstants.FlatShareShifts.ToString(CultureInfo.InvariantCulture)} shifts each, so at "
            + "least "
            + (Scenario5SpecConstants.TotalDayShifts - (2 * Scenario5SpecConstants.FlatShareShifts))
                .ToString(CultureInfo.InvariantCulture)
            + " of them necessarily go to employees who never asked for one — a soft preference, not a violation");

        WriteFreeAdjustedCeilings(artifactName, definition);
    }

    /// <summary>
    /// Writes the framing every scenario-5 report needs next to the numbers: the slot order the pinned
    /// ids produce and the measured headline figures.
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
            + $"{definition.Context.ShiftPreferences.Count.ToString(CultureInfo.InvariantCulture)} preference rows, "
            + $"{definition.Context.ScheduleCommands.Count.ToString(CultureInfo.InvariantCulture)} command rows, "
            + $"population {definition.Config.PopulationSize.ToString(CultureInfo.InvariantCulture)}, generations "
            + definition.Config.MaxGenerations.ToString(CultureInfo.InvariantCulture));

        TestContext.Out.WriteLine(
            "auction order inside one day (identical dates, so start time and then the pinned ids decide): "
            + AutofillShiftCatalog.DescribeAuctionOrderOfOneDay(definition.OrderCount, definition.HasDayShifts));

        TestContext.Out.WriteLine(
            "measured hours: " + Scenario5Diagnostics.DescribeHours(run.Metrics.Hours.PerEmployee));
        TestContext.Out.WriteLine(
            "measured free blocks: "
            + Scenario5Diagnostics.DescribeFreeBlocks(run.Metrics.Packages.FreeBlockHistogram));
        TestContext.Out.WriteLine(
            "measured day-shift shares: "
            + Scenario5Diagnostics.DescribeDayShiftShares(run.Metrics.DayShift.ShareByEmployee));
        TestContext.Out.WriteLine(
            "measured keyword windows: " + Scenario5Diagnostics.DescribeWindows(run.Metrics.Keyword.Windows));
        TestContext.Out.WriteLine(
            "measured blacklist violations: "
            + Scenario5Diagnostics.DescribeBlacklistViolations(run.Metrics.Preferences.BlacklistViolations));
        TestContext.Out.WriteLine(
            "measured carry-in: " + Scenario5Diagnostics.DescribeCarryIn(run.Metrics.CarryInThreeDimensional));

        WriteWhichTopDownResolutionHappened(run);

        foreach (var path in run.ArtifactPaths)
        {
            TestContext.Out.WriteLine("artifact: " + path);
        }
    }

    /// <summary>
    /// Reports which of the two top-down resolutions the algorithm produced. It is a MEASUREMENT and
    /// never an assertion: the specification states both as references and leaves the choice to the
    /// algorithm, so naming a winner in an assertion would decide what the test was asked to observe.
    /// </summary>
    /// <param name="run">Result of the deterministic runner</param>
    private static void WriteWhichTopDownResolutionHappened(DeterministicRunResult run)
    {
        var fullyMet = run.Metrics.Hours.PerEmployee
            .Count(e => e.PlannedHours + 1e-9 >= e.GuaranteedHours);
        var zeroHour = run.Metrics.Hours.ZeroHourEmployees.Count;
        var spread = run.Metrics.Hours.PerEmployee.Count == 0
            ? 0
            : run.Metrics.Hours.PerEmployee.Max(e => e.PlannedHours)
                - run.Metrics.Hours.PerEmployee.Min(e => e.PlannedHours);

        TestContext.Out.WriteLine(
            "measured top-down resolution: "
            + $"{fullyMet.ToString(CultureInfo.InvariantCulture)} rank(s) reach the full guarantee (reference (a) "
            + $"predicts {Scenario5SpecConstants.FullGuaranteeRanks.ToString(CultureInfo.InvariantCulture)}), "
            + $"{zeroHour.ToString(CultureInfo.InvariantCulture)} rank(s) hold nothing, hour spread "
            + $"{spread.ToString("0.#", CultureInfo.InvariantCulture)} h (reference (b) predicts a spread near zero "
            + $"at {Scenario5SpecConstants.FlatShareHours.ToString("0.#", CultureInfo.InvariantCulture)} h each)");
    }

    private static void WriteFreeAdjustedCeilings(string artifactName, AutofillScenarioDefinition definition)
    {
        var commands = definition.ScheduleCommands;
        if (commands is null || commands.IsEmpty)
        {
            TestContext.Out.WriteLine(
                $"{artifactName} FREE-adjusted ceilings: none — this run carries no keyword windows, so every rank "
                + "can reach the unadjusted ceiling and a top-down gap cannot be blamed on a FREE window.");
            return;
        }

        var affected = commands.Commands
            .Where(c => c.Keyword == ScheduleCommandKeyword.Free)
            .GroupBy(c => c.AgentId, StringComparer.Ordinal)
            .Select(g => (Employee: g.Key, Days: g.SelectMany(c => c.Days()).Distinct().Count()))
            .OrderBy(e => definition.ListRankOf(e.Employee))
            .ToList();

        if (affected.Count == 0)
        {
            TestContext.Out.WriteLine(
                $"{artifactName} FREE-adjusted ceilings: no FREE window in this run.");
            return;
        }

        foreach (var entry in affected)
        {
            TestContext.Out.WriteLine(
                $"{artifactName} FREE-adjusted ceiling for {entry.Employee} (rank "
                + $"{definition.ListRankOf(entry.Employee).ToString(CultureInfo.InvariantCulture)}): "
                + Scenario5Diagnostics.DescribeFreeAdjustedCeiling(entry.Days, AutofillSpecConstants.PeriodDays));
        }

        TestContext.Out.WriteLine(
            $"{artifactName} note on S5-7: the ranks above are the ones a FREE window shortens, and they are the "
            + "TOP ranks, which the top-down rule serves first. A fulfilment gap between them and the rank below is "
            + "therefore expected from the fixture and is NOT by itself a top-down defect. The assertion is written "
            + "as the specification states it; this line is what tells the two causes apart.");
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
