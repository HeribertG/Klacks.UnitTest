// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Analysis;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Support;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario2;

/// <summary>
/// Specification scenario 2 — the transition out of the previous month. Same period, demand and
/// roster as scenario 1, but February 2026 already holds work packages that reach into March, so the
/// autofill has to continue both the shift kind and the started package length across the month
/// boundary.
/// <para>
/// One shared run. The scenario is built and executed once in <c>[OneTimeSetUp]</c> and every
/// assertion of A1 to A14 reads the same cached result, which is what makes the PASS/FAIL table of
/// phase D a statement about one plan rather than about fourteen different ones. All artifacts are
/// therefore written under a single literal name.
/// </para>
/// <para>
/// A15 (recovery comparison run) is deliberately not implemented. Decision 5 in the header of
/// tests/autofill/SPEC.md: recovery is a failure repair, not a month-transition mechanism, and A15 is
/// carried in phase D as NOT-EVALUABLE, category c.
/// </para>
/// <para>
/// Expectations come from the specification, never from observed behaviour. A10 to A13 are known to
/// fail: the engine reads the previous month only in the auction's runtime state and no fitness stage
/// ever measures the continuation, so the assertions cite the same measurement on the auction seed
/// plan to show whether the continuation was never built or built and then dissolved.
/// </para>
/// </summary>
[TestFixture]
[Category("Autofill")]
public sealed class Scenario2Tests
{
    private const string ScenarioName = "scenario2";

    private const string ArtifactTestName = "Scenario2";

    private const string ProblemSeparator = " | ";

    private const double ShareComparisonEpsilon = 1e-9;

    private const double HoursComparisonEpsilon = 1e-9;

    private const string BoundaryTruncationNote =
        "Note on measurement: the analyzer builds packages from in-period days only, so a package carried over the "
        + "month boundary is truncated at 2026-03-01 — MA-1 is measured as at most 3 days instead of 5, MA-3 as at "
        + "most 4, and MA-2's single remaining day appears as a package of length 1. That shortens buckets and feeds "
        + "the short-package share; it can never create a package longer than 5.";

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

    private AutofillMetrics Metrics => Run.Metrics;

    [OneTimeSetUp]
    public void BuildAndRunScenarioTwo()
    {
        try
        {
            _definition = Scenario2CarryInFixture.Build();
        }
        catch (Exception exception)
        {
            _fixtureProblems = [$"scenario 2 could not be assembled: {exception.Message}"];
            return;
        }

        _fixtureProblems = Scenario2CarryInFixture.Validate(_definition);
        if (_fixtureProblems.Count > 0)
        {
            return;
        }

        _run = DeterministicRunner.Run(_definition, ScenarioName, ArtifactTestName);
        WriteDiagnosis(_definition, _run);
    }

    [Test]
    public void A1_EveryShiftOfThePeriodIsStaffed()
    {
        var coverage = Metrics.Coverage;

        coverage.UnfilledShifts.ShouldBeEmpty(
            $"A1: all {coverage.TotalRequiredShifts.ToString(CultureInfo.InvariantCulture)} demanded shifts of "
            + $"{Definition.PeriodFrom:yyyy-MM-dd}..{Definition.PeriodUntil:yyyy-MM-dd} must be staffed, but "
            + $"{coverage.UnfilledShifts.Count.ToString(CultureInfo.InvariantCulture)} are empty (filled: "
            + $"{coverage.FilledShifts.ToString(CultureInfo.InvariantCulture)}). Empty slots: "
            + Scenario2Diagnostics.DescribeUnfilled(coverage.UnfilledShifts));
    }

    [Test]
    public void A2_NobodyIsBookedTwiceOnTheSameDay()
    {
        var coverage = Metrics.Coverage;

        coverage.DoubleBookings.ShouldBeEmpty(
            "A2: no employee may hold two shifts on the same calendar day, but "
            + $"{coverage.DoubleBookings.Count.ToString(CultureInfo.InvariantCulture)} day(s) are double booked: "
            + Scenario2Diagnostics.DescribeDoubleBookings(coverage.DoubleBookings));
    }

    [Test]
    public void A3_RestTimeAndNightToEarlyAreRespected()
    {
        var legality = Metrics.Legality;
        var problems = new List<string>();

        if (legality.NightToEarlyViolations.Count > 0)
        {
            problems.Add(
                $"{legality.NightToEarlyViolations.Count.ToString(CultureInfo.InvariantCulture)} night-to-early "
                + $"transition(s): {Scenario2Diagnostics.DescribeNightToEarly(legality.NightToEarlyViolations)}");
        }

        if (legality.RestViolations.Count > 0)
        {
            problems.Add(
                $"{legality.RestViolations.Count.ToString(CultureInfo.InvariantCulture)} shift pair(s) below the "
                + $"contractual rest time of {AutofillSpecConstants.MinRestHours.ToString(CultureInfo.InvariantCulture)} h: "
                + Scenario2Diagnostics.DescribeRestViolations(legality.RestViolations));
        }

        problems.ShouldBeEmpty(
            "A3: the plan must contain neither a rest-time violation nor a night shift followed by an early shift. "
            + "The carry-in days of February take part in this check, so the seam at 2026-03-01 is covered. "
            + Describe(problems));
    }

    [Test]
    public void A4_ShiftKindStaysConstantInsideAPackage()
    {
        var packages = Metrics.Packages;
        var mixed = packages.Items.Where(p => p.MixedTypes).ToList();

        packages.MixedTypeCount.ShouldBe(
            0,
            "A4: the shift kind must stay constant inside a package and may only change at a package border, but "
            + $"{packages.MixedTypeCount.ToString(CultureInfo.InvariantCulture)} package(s) mix kinds: "
            + Scenario2Diagnostics.DescribePackages(mixed));
    }

    [Test]
    public void A5_PackageLengthsFollowTheFiveTwoIdeal()
    {
        var packages = Metrics.Packages;
        var histogram = packages.LengthHistogram;
        var modeBucket = AutofillSpecConstants.ExpectedPackageLengthMode.ToString(CultureInfo.InvariantCulture);
        var histogramText = Scenario2Diagnostics.DescribeHistogram(histogram);
        var problems = new List<string>();

        // Same reading as scenario 1 (Scenario1AssertionsBase): bucket 5 must be the single most
        // frequent length, so a tie at the maximum fails both scenarios identically and the A5 verdicts
        // of the two scenarios stay comparable.
        histogram.TryGetValue(modeBucket, out var countAtMode);
        if (packages.Items.Count == 0)
        {
            problems.Add($"the plan contains no package at all, so it cannot peak at {modeBucket} days");
        }
        else
        {
            var atLeastAsFrequent = histogram
                .Where(entry => !string.Equals(entry.Key, modeBucket, StringComparison.Ordinal))
                .Where(entry => entry.Value >= countAtMode)
                .Select(entry => $"{entry.Key} days:{entry.Value.ToString(CultureInfo.InvariantCulture)}")
                .ToList();
            if (atLeastAsFrequent.Count > 0)
            {
                problems.Add(
                    $"length {modeBucket} holds {countAtMode.ToString(CultureInfo.InvariantCulture)} package(s) and "
                    + $"is not the single most frequent length; at least as frequent: "
                    + string.Join(", ", atLeastAsFrequent));
            }
        }

        var shortPackages = packages.Items
            .Where(p => p.LengthDays <= AutofillSpecConstants.ShortPackageMaxLength)
            .ToList();
        var shortShareLimit = AutofillSpecConstants.ShortPackageShareLimit + AutofillSpecConstants.ShortPackageShareTolerance;
        var shortShare = packages.Items.Count == 0
            ? 0
            : (double)shortPackages.Count / packages.Items.Count;
        if (shortShare > shortShareLimit + ShareComparisonEpsilon)
        {
            problems.Add(
                $"{(shortShare * 100).ToString("0.#", CultureInfo.InvariantCulture)} % of the packages are at most "
                + $"{AutofillSpecConstants.ShortPackageMaxLength.ToString(CultureInfo.InvariantCulture)} days long, "
                + $"above the limit of {(shortShareLimit * 100).ToString("0.#", CultureInfo.InvariantCulture)} %: "
                + Scenario2Diagnostics.DescribePackages(shortPackages));
        }

        var tooLong = packages.Items
            .Where(p => p.LengthDays > AutofillSpecConstants.MaxAllowedPackageLength)
            .ToList();
        if (tooLong.Count > 0)
        {
            problems.Add(
                $"{tooLong.Count.ToString(CultureInfo.InvariantCulture)} package(s) exceed the maximum length of "
                + $"{AutofillSpecConstants.MaxAllowedPackageLength.ToString(CultureInfo.InvariantCulture)} days: "
                + Scenario2Diagnostics.DescribePackages(tooLong));
        }

        problems.ShouldBeEmpty(
            $"A5: package lengths must peak at {modeBucket} days, keep short packages below "
            + $"{(shortShareLimit * 100).ToString("0.#", CultureInfo.InvariantCulture)} % and never exceed "
            + $"{AutofillSpecConstants.MaxAllowedPackageLength.ToString(CultureInfo.InvariantCulture)} days. "
            + $"Histogram: {histogramText}. {BoundaryTruncationNote} {Describe(problems)}");
    }

    [Test]
    public void A6_GuaranteedHoursAreServedTopDown()
    {
        var hours = Metrics.Hours;
        var threshold = AutofillSpecConstants.ReferenceHoursTopRanks * AutofillSpecConstants.FulfilmentThreshold;
        var problems = new List<string>();

        if (hours.MonotonicityViolations.Count > 0)
        {
            problems.Add(
                "the fulfilment rises against the list order at "
                + Scenario2Diagnostics.DescribeMonotonicity(hours.MonotonicityViolations));
        }

        var belowReference = hours.PerEmployee
            .Where(e => e.ListRank <= AutofillSpecConstants.TopRankUpperBound)
            .Where(e => e.PlannedHours + HoursComparisonEpsilon < threshold)
            .ToList();
        if (belowReference.Count > 0)
        {
            problems.Add(
                $"ranks 1 to {AutofillSpecConstants.TopRankUpperBound.ToString(CultureInfo.InvariantCulture)} must "
                + $"each reach {threshold.ToString("0.#", CultureInfo.InvariantCulture)} h, that is "
                + $"{(AutofillSpecConstants.FulfilmentThreshold * 100).ToString("0.#", CultureInfo.InvariantCulture)} % "
                + $"of the reference {AutofillSpecConstants.ReferenceHoursTopRanks.ToString("0.#", CultureInfo.InvariantCulture)} h "
                + $"({AutofillSpecConstants.ReferenceShiftsTopRanks.ToString(CultureInfo.InvariantCulture)} shifts), but "
                + Scenario2Diagnostics.DescribeHours(belowReference) + " stay below");
        }

        problems.ShouldBeEmpty(
            "A6: the guaranteed hours must be served in list order, so the fulfilment never rises going down the "
            + $"list and the top ranks reach the top-down reference. All ranks: "
            + $"{Scenario2Diagnostics.DescribeHours(hours.PerEmployee)}. The February hours are deliberately kept "
            + "out of CurrentHours so this stays comparable to scenario 1. " + Describe(problems));
    }

    [Test]
    public void A7_RotationRunsForward()
    {
        var rotation = Metrics.Rotation;
        var backward = rotation.Transitions.Where(t => !t.Forward).ToList();

        rotation.ForwardRate.ShouldBeGreaterThanOrEqualTo(
            AutofillSpecConstants.MinForwardRotationRate,
            "A7: at least "
            + $"{(AutofillSpecConstants.MinForwardRotationRate * 100).ToString("0.#", CultureInfo.InvariantCulture)} % "
            + "of the package transitions must follow early to late to night to early, but only "
            + $"{(rotation.ForwardRate * 100).ToString("0.#", CultureInfo.InvariantCulture)} % of "
            + $"{rotation.Transitions.Count.ToString(CultureInfo.InvariantCulture)} transition(s) do. The "
            + $"{rotation.BackwardOrSkipCount.ToString(CultureInfo.InvariantCulture)} backward or skipping "
            + $"transition(s) each need a manual verdict: {Scenario2Diagnostics.DescribeTransitions(backward)}. "
            + "The analyzer cannot derive whether the forward successor was available, so transitions[].forced is "
            + "always false and 'justifiable as forced' is not machine-checkable.");
    }

    [Test]
    public void A8_ShiftKindsAreSpreadEvenlyOverTheTopRanks()
    {
        var counts = Metrics.Fairness.ShiftTypeCountPerEmployee
            .Where(c => c.ListRank <= AutofillSpecConstants.TopRankUpperBound)
            .ToList();
        var spread = AutofillPlanAnalyzer.SpreadOf(counts);
        var widest = Math.Max(spread.Early, Math.Max(spread.Late, spread.Night));

        widest.ShouldBeLessThanOrEqualTo(
            AutofillSpecConstants.MaxShiftKindSpread,
            $"A8: over ranks 1 to {AutofillSpecConstants.TopRankUpperBound.ToString(CultureInfo.InvariantCulture)} "
            + "the difference between the highest and the lowest count of a shift kind must stay at most "
            + $"{AutofillSpecConstants.MaxShiftKindSpread.ToString(CultureInfo.InvariantCulture)}, but it is "
            + $"early={spread.Early.ToString(CultureInfo.InvariantCulture)}, "
            + $"late={spread.Late.ToString(CultureInfo.InvariantCulture)}, "
            + $"night={spread.Night.ToString(CultureInfo.InvariantCulture)}. Counts: "
            + $"{Scenario2Diagnostics.DescribeShiftCounts(counts)}. Rank "
            + $"{AutofillSpecConstants.EmployeeCount.ToString(CultureInfo.InvariantCulture)} is excluded because A6 "
            + "expects it to be left with the remainder.");
    }

    [Test]
    public void A9_TwoRunsWithTheSameSeedProduceTheSamePlan()
    {
        Run.RunsIdentical.ShouldBeTrue(
            $"A9: two runs with seed {Definition.Config.RandomSeed.ToString(CultureInfo.InvariantCulture)}, "
            + "sequential evaluation and no wall-clock budget must produce the identical plan, but they differ at "
            + $"{Run.FirstDifference}");
    }

    [Test]
    public void A10_CarriedInPackagesKeepTheirShiftKind()
    {
        var openEmployees = Definition.OpenCarryIns.Select(c => c.AgentId).ToList();
        var measured = CarryInEntriesOf(openEmployees);
        var wrong = measured.Where(c => c.ActualFirstShiftType != c.ExpectedShiftType).ToList();

        wrong.ShouldBeEmpty(
            "A10: an employee still inside a previous-month package must start the period with the very shift kind "
            + $"of that package (MA-1 night, MA-2 late, MA-3 early), but "
            + $"{wrong.Count.ToString(CultureInfo.InvariantCulture)} of "
            + $"{measured.Count.ToString(CultureInfo.InvariantCulture)} do not: "
            + $"{Scenario2Diagnostics.DescribeCarryIn(wrong)}. Same measurement on the auction seed plan, before any "
            + $"evolution ran: {Scenario2Diagnostics.DescribeSeedCarryIn(Run.SeedMetrics, openEmployees)}. An "
            + "identical failure there places the defect in the seeding, not in the evolution.");
    }

    [Test]
    public void A11_CarriedInPackagesAreCompletedToFiveShifts()
    {
        var openEmployees = Definition.OpenCarryIns.Select(c => c.AgentId).ToList();
        var measured = CarryInEntriesOf(openEmployees);
        var wrong = measured.Where(c => c.ActualRemainingDays != c.ExpectedRemainingDays).ToList();

        wrong.ShouldBeEmpty(
            "A11: a started package must be completed to exactly "
            + $"{AutofillSpecConstants.MaxWorkDays.ToString(CultureInfo.InvariantCulture)} shifts instead of being "
            + $"started anew — MA-1 owes {Scenario2SpecValues.Ma1RemainingDays.ToString(CultureInfo.InvariantCulture)} "
            + $"more nights, MA-2 {Scenario2SpecValues.Ma2RemainingDays.ToString(CultureInfo.InvariantCulture)} more "
            + $"late shift, MA-3 {Scenario2SpecValues.Ma3RemainingDays.ToString(CultureInfo.InvariantCulture)} more "
            + $"early shifts. Wrong: {Scenario2Diagnostics.DescribeCarryIn(wrong)}. Same measurement on the auction "
            + $"seed plan: {Scenario2Diagnostics.DescribeSeedCarryIn(Run.SeedMetrics, openEmployees)}. An identical "
            + "failure there places the defect in the seeding, not in the evolution.");
    }

    [Test]
    public void A12_TwoFreeDaysFollowTheCompletedCarryInPackage()
    {
        var problems = new List<string>();

        foreach (var carryIn in Definition.OpenCarryIns)
        {
            var packages = Scenario2Diagnostics.PackagesOf(Metrics, carryIn.AgentId);
            var continued = packages.FirstOrDefault(p => p.StartDate == Definition.PeriodFrom);
            if (continued is null)
            {
                problems.Add(
                    $"{carryIn.AgentId} has no in-period package starting on {Definition.PeriodFrom:yyyy-MM-dd}, so "
                    + $"the carried-in package was not continued at all; its packages are "
                    + $"{Scenario2Diagnostics.DescribePackages(packages)}");
                continue;
            }

            var next = packages.FirstOrDefault(p => p.StartDate > continued.EndDate);
            var freeDays = next is null
                ? Definition.PeriodUntil.DayNumber - continued.EndDate.DayNumber
                : next.StartDate.DayNumber - continued.EndDate.DayNumber - 1;
            if (freeDays < AutofillSpecConstants.MinFreeDaysAfterCarryIn)
            {
                var bound = next is null
                    ? $"the period ends on {Definition.PeriodUntil:yyyy-MM-dd}"
                    : $"the next package starts on {next.StartDate:yyyy-MM-dd}";
                problems.Add(
                    $"{carryIn.AgentId}: continued package {Scenario2Diagnostics.PackageLine(continued)} is followed "
                    + $"by only {freeDays.ToString(CultureInfo.InvariantCulture)} free day(s) because {bound}");
            }
        }

        problems.ShouldBeEmpty(
            $"A12: at least {AutofillSpecConstants.MinFreeDaysAfterCarryIn.ToString(CultureInfo.InvariantCulture)} "
            + "consecutive free days must follow the completion of a carried-in package. Measured against the "
            + "in-period package that starts on the first day of the period; the February part of that package is "
            + $"outside the analyzer's package view. {Describe(problems)}");
    }

    [Test]
    public void A13_ClosedPackagesRotateAcrossTheMonthBoundary()
    {
        var closed = Definition.CarryIns
            .Where(c => !c.IsOpenAt(Definition.PeriodFrom))
            .ToList();
        var problems = new List<string>();

        foreach (var carryIn in closed)
        {
            var measured = Metrics.CarryIn
                .FirstOrDefault(c => string.Equals(c.Employee, carryIn.AgentId, StringComparison.Ordinal));
            if (measured is null)
            {
                problems.Add($"{carryIn.AgentId} was not measured at all, which means the fixture and the metrics disagree");
                continue;
            }

            if (measured.ActualFirstShiftType == carryIn.ExpectedFirstShiftKind)
            {
                continue;
            }

            var firstPackage = Scenario2Diagnostics.FirstPackageOf(Metrics, carryIn.AgentId);
            problems.Add(
                $"{carryIn.AgentId} closed a {carryIn.Kind} package on {carryIn.PackageEndInclusive:yyyy-MM-dd}, so "
                + $"its first package of the period must rotate to {carryIn.ExpectedFirstShiftKind}, but it is "
                + $"{(measured.ActualFirstShiftType?.ToString() ?? "absent — the employee received no in-period shift")} "
                + $"({Scenario2Diagnostics.PackageLine(firstPackage)})");
        }

        problems.ShouldBeEmpty(
            "A13: a package that ended in the previous month must not restart at early; MA-4 rotates early to late "
            + $"and is expected to start again on {Scenario2SpecValues.Ma4ExpectedNewPackageStart:yyyy-MM-dd}, MA-5 "
            + $"rotates late to night and is expected to start again on "
            + $"{Scenario2SpecValues.Ma5ExpectedNewPackageStart:yyyy-MM-dd} — the assertion itself only judges the "
            + $"shift kind. Same measurement on the auction seed plan: "
            + $"{Scenario2Diagnostics.DescribeSeedCarryIn(Run.SeedMetrics, closed.Select(c => c.AgentId))}. "
            + Describe(problems));
    }

    [Test]
    public void A14_NothingBeforeThePeriodIsChanged()
    {
        var problems = new List<string>();

        if (!Run.CarryInUnchanged)
        {
            problems.Add($"the fixed previous-month works differ after the run: {Run.CarryInDifference}");
        }

        var outsidePeriod = AutofillPlanAnalyzer.TokensOutsidePeriod(Run.Plan, Definition);
        if (outsidePeriod.Count > 0)
        {
            problems.Add(
                $"{outsidePeriod.Count.ToString(CultureInfo.InvariantCulture)} assignment(s) carry a date outside the "
                + $"period: {string.Join("; ", outsidePeriod.Select(t => $"{t.AgentId} {t.Date:yyyy-MM-dd}"))}");
        }

        problems.ShouldBeEmpty(
            "A14: no shift dated before 2026-03-01 may be changed. Measured as the fixed February works being "
            + "byte-identical before and after the run — agent, day, span, hours, shift reference and work id — plus "
            + "no assignment being planned outside the period. The previous month is input only and never appears in "
            + $"the result scenario, so it cannot be compared inside the output. {Describe(problems)}");
    }

    private static string Describe(IReadOnlyList<string> problems)
        => problems.Count == 0 ? string.Empty : "Problems: " + string.Join(ProblemSeparator, problems);

    private static void WriteDiagnosis(AutofillScenarioDefinition definition, DeterministicRunResult run)
    {
        TestContext.Out.WriteLine(
            $"scenario 2, seed {definition.Config.RandomSeed.ToString(CultureInfo.InvariantCulture)}, "
            + $"{definition.Context.BoundaryLockedWorks.Count.ToString(CultureInfo.InvariantCulture)} carry-in days");
        TestContext.Out.WriteLine(
            "final plan carry-in: " + Scenario2Diagnostics.DescribeCarryIn(run.Metrics.CarryIn));
        TestContext.Out.WriteLine(
            "auction seed carry-in: " + Scenario2Diagnostics.DescribeCarryIn(run.SeedMetrics.CarryIn));
        TestContext.Out.WriteLine(
            "final idealShare="
            + run.Metrics.Packages.IdealShare.ToString("0.###", CultureInfo.InvariantCulture)
            + " seed idealShare="
            + run.SeedMetrics.Packages.IdealShare.ToString("0.###", CultureInfo.InvariantCulture));

        foreach (var path in run.ArtifactPaths)
        {
            TestContext.Out.WriteLine("artifact: " + path);
        }
    }

    private IReadOnlyList<CarryInRespect> CarryInEntriesOf(IReadOnlyList<string> employees)
    {
        var wanted = employees.ToHashSet(StringComparer.Ordinal);
        return Metrics.CarryIn.Where(c => wanted.Contains(c.Employee)).ToList();
    }

    private void EnsureFixtureIsValid()
    {
        if (_fixtureProblems.Count > 0)
        {
            Assert.Inconclusive(
                "fixture invalid — scenario 2 was not run, so no rule assertion can be judged: "
                + string.Join(ProblemSeparator, _fixtureProblems));
        }
    }
}
