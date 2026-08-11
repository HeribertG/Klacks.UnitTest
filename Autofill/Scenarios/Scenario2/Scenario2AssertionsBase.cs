// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Analysis;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Support;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario2;

/// <summary>
/// Assertions A1 to A14 of the carry-in family, shared by scenario 2 (the plain month transition)
/// and scenario 3 (the same fixture plus keyword restrictions, whose specification inherits A1 to
/// A14 unchanged onto its main run L1). Extracted verbatim from <see cref="Scenario2Tests"/> on
/// 2026-08-08 — the assertion texts, tolerances and measurements are unchanged, so scenario 2 keeps
/// judging exactly what it judged before the extraction.
/// <para>
/// A deriving fixture builds and runs its scenario once in its own <c>[OneTimeSetUp]</c> and exposes
/// the cached result through <see cref="Definition"/> and <see cref="Run"/>; both accessors are
/// expected to report an invalid fixture (for example via <c>Assert.Inconclusive</c>) instead of
/// returning a half-built scenario, which keeps a setup mistake from ever being read as a verdict
/// about the algorithm.
/// </para>
/// <para>
/// A15 (recovery comparison run) is deliberately not implemented in any deriving fixture. Decision 5
/// in the header of tests/autofill/SPEC.md: recovery is a failure repair, not a month-transition
/// mechanism, and A15 is carried in phase D as NOT-EVALUABLE, category c.
/// </para>
/// </summary>
public abstract class Scenario2AssertionsBase : AutofillBaselineTestBase
{
    /// <summary>Separator between the individual problem lines of one assertion message.</summary>
    protected const string ProblemSeparator = " | ";

    private const double ShareComparisonEpsilon = 1e-9;

    private const double HoursComparisonEpsilon = 1e-9;

    private const string BoundaryPackageNote =
        "Note on measurement: packages span the month boundary, so a run that started in February and reaches into "
        + "March is one package with one length — which means a package may exceed five days precisely because the "
        + "February part is counted. Packages already closed before 2026-03-01 are not listed, and the free days "
        + "before the first listed package stay the leading free edge rather than becoming a free block.";

    /// <summary>The scenario of the run the assertions read; guards its own fixture validity.</summary>
    protected abstract AutofillScenarioDefinition Definition { get; }

    /// <summary>The cached run of the fixture; guards its own fixture validity.</summary>
    protected abstract DeterministicRunResult Run { get; }

    /// <summary>Measurement of run 1 — the plan every assertion judges.</summary>
    protected AutofillMetrics Metrics => Run.Metrics;

    /// <summary>The no-regression floor reads the same cached measurement as the rule assertions.</summary>
    protected override AutofillMetrics BaselineMetrics => Metrics;

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
            "A2: one employee may hold two shifts on the same calendar day as long as they are different shifts "
            + "that do not overlap; only the same shift assigned twice and two shifts overlapping in time are "
            + $"conflicts, but {coverage.DoubleBookings.Count.ToString(CultureInfo.InvariantCulture)} conflict(s) "
            + "were found: " + Scenario2Diagnostics.DescribeDoubleBookings(coverage.DoubleBookings));
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
    [Category(AutofillTestCategories.SpecFirstRed)]
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
    [Category(AutofillTestCategories.SpecFirstRed)]
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
            + $"Histogram: {histogramText}. {BoundaryPackageNote} {Describe(problems)}");
    }

    [Test]
    [Category(AutofillTestCategories.SpecFirstRed)]
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
    [Category(AutofillTestCategories.SpecFirstRed)]
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
            + "true only where a fixture ban list proves the forward kind was closed; everything else is not "
            + "machine-checkable.");
    }

    [Test]
    [Category(AutofillTestCategories.SpecFirstRed)]
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

    /// <summary>
    /// Core of assertion A12, shared by both heirs. Not a [Test] here on purpose: A12 is spec-first
    /// red in scenario 2 but green in scenario 3, so each heir declares its own [Test] wrapper and
    /// only the scenario-2 wrapper carries the SpecFirstRed category. Moving the [Test] back to this
    /// base class would drag the green scenario-3 run out of the CI deploy gate as well.
    /// </summary>
    protected void AssertA12TwoFreeDaysFollowTheCompletedCarryInPackage()
    {
        var problems = new List<string>();

        foreach (var carryIn in Definition.OpenCarryIns)
        {
            var packages = Scenario2Diagnostics.PackagesOf(Metrics, carryIn.AgentId);
            var continued = packages.FirstOrDefault(
                p => p.StartDate <= Definition.PeriodFrom && p.EndDate >= Definition.PeriodFrom);
            if (continued is null)
            {
                problems.Add(
                    $"{carryIn.AgentId} holds no shift on {Definition.PeriodFrom:yyyy-MM-dd}, so the carried-in "
                    + $"package was not continued at all; its packages are "
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
            + "consecutive free days must follow the completion of a carried-in package. Measured on the whole "
            + "package that covers the first day of the period, February part included, so the length quoted below "
            + $"is the real length of the continued package. {Describe(problems)}");
    }

    [Test]
    [Category(AutofillTestCategories.SpecFirstRed)]
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

    /// <summary>One line out of the collected problem texts; empty when there is nothing to report.</summary>
    /// <param name="problems">Problem lines of one assertion</param>
    protected static string Describe(IReadOnlyList<string> problems)
        => problems.Count == 0 ? string.Empty : "Problems: " + string.Join(ProblemSeparator, problems);

    /// <summary>Carry-in measurements of the given employees, in metric order.</summary>
    /// <param name="employees">Employees to select</param>
    protected IReadOnlyList<CarryInRespect> CarryInEntriesOf(IReadOnlyList<string> employees)
    {
        var wanted = employees.ToHashSet(StringComparer.Ordinal);
        return Metrics.CarryIn.Where(c => wanted.Contains(c.Employee)).ToList();
    }
}
