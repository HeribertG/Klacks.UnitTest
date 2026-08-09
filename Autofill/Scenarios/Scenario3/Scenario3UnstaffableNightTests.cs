// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario2;
using Klacks.UnitTest.Autofill.Support;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario3;

/// <summary>
/// The engine half of assertion A22 on run L4: NACHT-BEF removed from all five employees (the
/// `Missing` branch of owner decision S3-1), so every night slot of the period has an empty
/// eligible pool. The specification demands that this unstaffability is DECLARED, never silently
/// papered over — 31 quietly staffed night shifts would be the gravest possible finding of the
/// suite, employees planned onto shifts they are not entitled to hold.
/// <para>
/// What "declared" means on engine level (finding K9): the slot stays empty and the emptiness is
/// visible in the coverage measurement; the engine raises no exception. The API-side signalling
/// (warnings, unfillable report) is out of scope here. A1 to A14 are deliberately NOT inherited onto
/// L4 — it is a diagnostic run whose demand is unsatisfiable by construction, so the coverage and
/// hour assertions of the base family would report the fixture, not the algorithm. The A26 slice for
/// L4 lives here because the runner executes this fixture's two runs.
/// </para>
/// </summary>
[TestFixture]
[Category("Autofill")]
public class Scenario3UnstaffableNightTests
{
    private const string ProblemSeparator = " | ";

    private AutofillScenarioDefinition? _definition;

    private DeterministicRunResult? _run;

    private IReadOnlyList<string> _fixtureProblems = [];

    private AutofillScenarioDefinition Definition
    {
        get
        {
            EnsureFixtureIsValid();
            return _definition!;
        }
    }

    private DeterministicRunResult Run
    {
        get
        {
            EnsureFixtureIsValid();
            return _run!;
        }
    }

    [OneTimeSetUp]
    public void BuildAndRunLFour()
    {
        try
        {
            _definition = Scenario3EligibilityFixture.BuildL4();
        }
        catch (Exception exception)
        {
            _fixtureProblems = [$"run L4 could not be assembled: {exception.Message}"];
            return;
        }

        var problems = new List<string>(
            Scenario3EligibilityFixture.Validate(_definition, Scenario3SpecValues.L4TripleCount));

        // Only the February stock is prechecked: the ban triples all lie in March, so the fixed
        // carry-in must stay conform, while the expected March continuations of MA-1 (night) and
        // MA-5 (rotated night) are unstaffable BY DESIGN of this run.
        problems.AddRange(Scenario3EligibilityFixture.CarryInStockConformityProblems(_definition));
        _fixtureProblems = problems;
        if (_fixtureProblems.Count > 0)
        {
            return;
        }

        _run = DeterministicRunner.Run(
            _definition, Scenario3SpecValues.ScenarioName, Scenario3SpecValues.L4ArtifactName);

        TestContext.Out.WriteLine(
            $"L4, seed {_definition.Config.RandomSeed.ToString(CultureInfo.InvariantCulture)}, ban triples "
            + $"{_definition.Eligibility!.IneligibleAssignments.Count.ToString(CultureInfo.InvariantCulture)}, "
            + $"unfilled {_run.Metrics.Coverage.UnfilledShifts.Count.ToString(CultureInfo.InvariantCulture)}, "
            + $"empty pools {_run.Metrics.Eligibility.EmptyPoolDays.Count.ToString(CultureInfo.InvariantCulture)}");
        foreach (var path in _run.ArtifactPaths)
        {
            TestContext.Out.WriteLine("artifact: " + path);
        }
    }

    [Test]
    public void A22_TheInputItselfDeclaresEveryNightPoolEmpty()
    {
        var emptyPools = Run.Metrics.Eligibility.EmptyPoolDays;
        var problems = new List<string>();

        var nonNight = emptyPools.Where(e => e.ShiftType != AutofillShiftKind.Night).ToList();
        if (nonNight.Count > 0)
        {
            problems.Add(
                $"{nonNight.Count.ToString(CultureInfo.InvariantCulture)} empty pool(s) on day shifts: "
                + string.Join(", ", nonNight.Select(e => $"{e.Date:yyyy-MM-dd} {e.ShiftType}")));
        }

        if (emptyPools.Count != Scenario3SpecValues.L4ExpectedUnfilledNights)
        {
            problems.Add(
                $"{emptyPools.Count.ToString(CultureInfo.InvariantCulture)} empty pool(s) instead of "
                + $"{Scenario3SpecValues.L4ExpectedUnfilledNights.ToString(CultureInfo.InvariantCulture)}");
        }

        problems.ShouldBeEmpty(
            "A22 (input side): with NACHT-BEF removed from everybody, exactly the "
            + $"{Scenario3SpecValues.L4ExpectedUnfilledNights.ToString(CultureInfo.InvariantCulture)} night slots "
            + "of the period must be unfillable by input — eligibility.emptyPoolDays is the declaration the "
            + "output assertions below measure the engine against. " + Describe(problems));
    }

    [Test]
    public void A22_AllThirtyOneNightsStayUnstaffedAndNothingElseDoes()
    {
        var coverage = Run.Metrics.Coverage;
        var problems = new List<string>();

        var unfilledNights = coverage.UnfilledShifts
            .Where(u => u.ShiftType == AutofillShiftKind.Night)
            .Select(u => u.Date)
            .ToHashSet();
        var missingDeclaredGaps = new List<string>();
        for (var date = Definition.PeriodFrom; date <= Definition.PeriodUntil; date = date.AddDays(1))
        {
            if (!unfilledNights.Contains(date))
            {
                missingDeclaredGaps.Add($"{date:yyyy-MM-dd}");
            }
        }

        if (missingDeclaredGaps.Count > 0)
        {
            problems.Add(
                $"{missingDeclaredGaps.Count.ToString(CultureInfo.InvariantCulture)} night slot(s) are NOT "
                + "reported unfilled although no employee is eligible — a silently staffed or silently swallowed "
                + "night: " + string.Join(", ", missingDeclaredGaps));
        }

        var unfilledDayShifts = coverage.UnfilledShifts
            .Where(u => u.ShiftType != AutofillShiftKind.Night)
            .ToList();
        if (unfilledDayShifts.Count > 0)
        {
            problems.Add(
                $"{unfilledDayShifts.Count.ToString(CultureInfo.InvariantCulture)} early/late slot(s) are "
                + "unfilled although their pools are untouched: "
                + Scenario2Diagnostics.DescribeUnfilled(unfilledDayShifts));
        }

        problems.ShouldBeEmpty(
            "A22 (output side): the undersupply must surface as exactly the "
            + $"{Scenario3SpecValues.L4ExpectedUnfilledNights.ToString(CultureInfo.InvariantCulture)} unstaffed "
            + "night slots while every early and late shift stays staffed — the restriction must not be silently "
            + "ignored (31 staffed nights would be the blocker of the suite) and must not bleed into shifts it "
            + $"does not concern. Unfilled total: "
            + $"{coverage.UnfilledShifts.Count.ToString(CultureInfo.InvariantCulture)}, filled: "
            + $"{coverage.FilledShifts.ToString(CultureInfo.InvariantCulture)} of "
            + $"{coverage.TotalRequiredShifts.ToString(CultureInfo.InvariantCulture)}. " + Describe(problems));
    }

    [Test]
    public void A22_NoEmployeeIsForceAssignedAgainstTheBanList()
    {
        var violations = Run.Metrics.Keyword.Violations;

        violations.ShouldBeEmpty(
            "A22 (no force-assign): not a single planned or carried-in shift may sit on a banned triple — an "
            + "engine that fills an unfillable slot anyway would plan employees onto shifts they are not entitled "
            + $"to hold. Violations: "
            + string.Join(ProblemSeparator, violations.Select(v =>
                $"{v.Employee} {v.Date:yyyy-MM-dd} {v.ShiftType} missing {v.MissingKeyword}")));
    }

    [Test]
    public void A26_LFourReproducesItself()
    {
        Run.RunsIdentical.ShouldBeTrue(
            $"A26 (L4 slice): two runs with seed {Definition.Config.RandomSeed.ToString(CultureInfo.InvariantCulture)}, "
            + "sequential evaluation and no wall-clock budget must produce the identical plan even when part of "
            + $"the demand is unfillable, but they differ at {Run.FirstDifference}");
    }

    private static string Describe(IReadOnlyList<string> problems)
        => problems.Count == 0 ? string.Empty : "Problems: " + string.Join(ProblemSeparator, problems);

    private void EnsureFixtureIsValid()
    {
        if (_fixtureProblems.Count > 0)
        {
            Assert.Inconclusive(
                "fixture invalid — run L4 was not executed, so no assertion can be judged: "
                + string.Join(ProblemSeparator, _fixtureProblems));
        }
    }
}
