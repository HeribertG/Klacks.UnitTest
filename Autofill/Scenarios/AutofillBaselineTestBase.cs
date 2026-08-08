// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Analysis;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios;

/// <summary>
/// The no-regression floor every scenario carries next to its specification assertions. The rule
/// assertions A1 to A14 state what the autofill SHOULD do and most of them are red; while a test is
/// red it warns about nothing, so a rewrite of the algorithm could halve the forward rotation rate
/// without a single test noticing. These <c>Baseline_</c> tests close that gap: they pin the state the
/// current engine actually reaches and turn red the moment it gets worse, while any improvement leaves
/// them green.
/// <para>
/// The pins are bands, not exact values. Each run is deterministic under its seed, but the seed itself
/// is an arbitrary starting point of the genetic search: measuring the same unchanged engine under the
/// seeds of <see cref="AutofillSeedBand.Seeds"/> moves several of the soft metrics further than a real
/// code change does. An exact pin therefore reports trajectory noise as a regression. Every constant
/// below is instead the worst value the current engine produced over those seeds, so a red means the
/// change made the plan worse than anything the engine reached before it — a statement about the code
/// rather than about the search path.
/// </para>
/// <para>
/// The assertions themselves still judge the run of <see cref="AutofillSpecConstants.RandomSeed"/>
/// alone and read the measurement their fixture already produced in its one-time setup, so they cost
/// no additional engine run. The further band seeds are measured once per fixture and only reported —
/// see <see cref="AutofillSeedBand"/>. The epsilon below absorbs floating-point representation, not
/// tolerance; the tolerance lives in the band.
/// </para>
/// </summary>
public abstract class AutofillBaselineTestBase
{
    private const double ComparisonEpsilon = 1e-9;

    private const string ShareFormat = "0.####";

    private const string HoursFormat = "0.##";

    /// <summary>Measurement of the run this fixture executed once in its setup.</summary>
    protected abstract AutofillMetrics BaselineMetrics { get; }

    /// <summary>The values this scenario pins as its floor.</summary>
    protected abstract AutofillBaseline Baseline { get; }

    /// <summary>
    /// The carry-in pin of this scenario, reported next to the shared seven; null in a scenario
    /// without a previous month, where the metric does not exist.
    /// </summary>
    protected virtual int? PinnedCarryInOkCount => null;

    [Test]
    public void Baseline_ForwardRotationRateDidNotFall()
    {
        var rotation = BaselineMetrics.Rotation;

        rotation.ForwardRate.ShouldBeGreaterThanOrEqualTo(
            Baseline.MinForwardRate - ComparisonEpsilon,
            $"Baseline: the forward rotation rate must not fall below the band floor {Share(Baseline.MinForwardRate)}, "
            + $"but it is {Share(rotation.ForwardRate)} "
            + $"({Count(rotation.Transitions.Count - rotation.BackwardOrSkipCount)} of "
            + $"{Count(rotation.Transitions.Count)} package transitions run early to late to night to early). "
            + "This is not the specification target of A7 — the floor is the lowest rate the current engine reached "
            + "over the band seeds, so a red here means the change rotates worse than any seed did before it.");
    }

    [Test]
    public void Baseline_MixedKindPackagesDidNotGrow()
    {
        var packages = BaselineMetrics.Packages;

        packages.MixedTypeCount.ShouldBeLessThanOrEqualTo(
            Baseline.MaxMixedTypeCount,
            $"Baseline: the number of packages that change shift kind inside the package must not rise above the "
            + $"band ceiling {Count(Baseline.MaxMixedTypeCount)}, but {Count(packages.MixedTypeCount)} of "
            + $"{Count(packages.Items.Count)} package(s) mix kinds. A4 demands 0; this ceiling is the highest count "
            + "the current engine reached over the band seeds and only guards against the number growing further.");
    }

    [Test]
    public void Baseline_ShortPackageShareDidNotGrow()
    {
        var share = AutofillPlanAnalyzer.ShortPackageShare(
            BaselineMetrics.Packages, AutofillSpecConstants.ShortPackageMaxLength);

        share.ShouldBeLessThanOrEqualTo(
            Baseline.MaxShortPackageShare + ComparisonEpsilon,
            $"Baseline: the share of packages of at most {Count(AutofillSpecConstants.ShortPackageMaxLength)} day(s) "
            + $"must not rise above the band ceiling {Share(Baseline.MaxShortPackageShare)}, but it is {Share(share)} of "
            + $"{Count(BaselineMetrics.Packages.Items.Count)} package(s). Splitting work into ever smaller pieces is "
            + "the failure mode this floor watches.");
    }

    [Test]
    public void Baseline_OverlongPackagesDidNotGrow()
    {
        var overlong = AutofillPlanAnalyzer.PackagesLongerThan(
            BaselineMetrics.Packages, AutofillSpecConstants.MaxAllowedPackageLength);

        overlong.ShouldBeLessThanOrEqualTo(
            Baseline.MaxPackagesOverIdealLength,
            $"Baseline: at most {Count(Baseline.MaxPackagesOverIdealLength)} package(s) may exceed the "
            + $"{Count(AutofillSpecConstants.MaxAllowedPackageLength)}-day ideal, but {Count(overlong)} do. "
            + "In a scenario with a carry-in month a package legitimately spans the month boundary, so the count "
            + "includes the February part of a continued package. Unlike the other seven this pin is a hard success "
            + "criterion of the operator rework, not a tolerated state: it is meant to stay sharp.");
    }

    [Test]
    public void Baseline_IdealFiveTwoShareDidNotFall()
    {
        var idealShare = BaselineMetrics.Packages.IdealShare;

        idealShare.ShouldBeGreaterThanOrEqualTo(
            Baseline.MinIdealShare - ComparisonEpsilon,
            $"Baseline: the share of packages of five days followed by exactly two free days must not fall below the "
            + $"band floor {Share(Baseline.MinIdealShare)}, but it is {Share(idealShare)} of "
            + $"{Count(BaselineMetrics.Packages.Items.Count)} package(s).");
    }

    [Test]
    public void Baseline_ShiftKindSpreadDidNotGrow()
    {
        var spread = BaselineMetrics.Fairness.SpreadPerType;
        var widest = Math.Max(spread.Early, Math.Max(spread.Late, spread.Night));

        widest.ShouldBeLessThanOrEqualTo(
            Baseline.MaxShiftKindSpread,
            $"Baseline: over all employees the widest difference between the highest and the lowest count of one "
            + $"shift kind must not rise above the band ceiling {Count(Baseline.MaxShiftKindSpread)}, but it is "
            + $"{Count(widest)} (early={Count(spread.Early)}, late={Count(spread.Late)}, "
            + $"night={Count(spread.Night)}). Unlike A8 this covers every rank, because an unfair rank 5 is still "
            + "unfairness and must not silently get worse.");
    }

    [Test]
    public void Baseline_HourMonotonicityViolationsDidNotGrow()
    {
        var violations = BaselineMetrics.Hours.MonotonicityViolations;

        violations.Count.ShouldBeLessThanOrEqualTo(
            Baseline.MaxMonotonicityViolations,
            $"Baseline: at most {Count(Baseline.MaxMonotonicityViolations)} rank(s) may reach a higher fulfilment "
            + $"than the rank above them, but {Count(violations.Count)} do: "
            + $"{string.Join("; ", violations.Select(v => $"rank {Count(v.Rank)} {Share(v.ThisPct)} over {Share(v.PrevPct)}"))}. "
            + "A6 demands none at all; this band ceiling is the highest count the current engine reached over the "
            + "band seeds and only guards the top-down order against decaying further.");
    }

    /// <summary>
    /// Rule 5 of the binding priority table — the guaranteed hours are served strictly top-down —
    /// outranks rule 6, package integrity, so hours sliding from the top ranks down to the last one is
    /// a regression at a high priority even when the plan still covers every shift and the total stays
    /// the same. Neither the per-rank fulfilment of A6 nor the monotonicity guard sees that: a plan can
    /// be perfectly monotone and still have moved work away from the top. This guard watches the one
    /// number that does.
    /// </summary>
    [Test]
    public void Baseline_TopRankPlannedHoursDidNotFall()
    {
        var planned = AutofillBandValues.TopRanksPlannedHoursOf(BaselineMetrics);
        var perRank = BaselineMetrics.Hours.PerEmployee
            .Select(e => $"{e.Employee} (rank {Count(e.ListRank)}) {Hours(e.PlannedHours)} h");

        planned.ShouldBeGreaterThanOrEqualTo(
            Baseline.MinTopRanksPlannedHours - ComparisonEpsilon,
            $"Baseline: ranks 1 to {Count(AutofillSpecConstants.TopRankUpperBound)} must together keep at least the "
            + $"band floor of {Hours(Baseline.MinTopRanksPlannedHours)} h, but they hold {Hours(planned)} h. "
            + $"Measured: {string.Join(", ", perRank)}. Rule 5 ranks the top-down service of the guaranteed hours "
            + "above rule 6, package integrity, so hours moving down the list is a regression even when nothing else "
            + "gets worse — a change may only ever lift this number.");
    }

    /// <summary>
    /// Measures the seed band of this scenario and writes it to the artifacts and the test output.
    /// Call it once from the fixture's one-time setup and only after the asserted run has finished, so
    /// the determinism verdict and the boundary fingerprint are already taken.
    /// </summary>
    /// <param name="definition">Scenario the asserted run used; only its seed is varied</param>
    /// <param name="assertedRunMetrics">Measurement of the asserted run</param>
    /// <param name="scenarioName">Scenario name; becomes the artifact folder</param>
    /// <param name="testName">Test name; becomes the artifact file name</param>
    protected void MeasureAndReportSeedBand(
        AutofillScenarioDefinition definition,
        AutofillMetrics assertedRunMetrics,
        string scenarioName,
        string testName)
        => AutofillSeedBand.MeasureAndReport(
            definition,
            assertedRunMetrics,
            AutofillSeedBand.PinnedValuesOf(Baseline, PinnedCarryInOkCount),
            scenarioName,
            testName);

    private static string Share(double value)
        => value.ToString(ShareFormat, CultureInfo.InvariantCulture);

    private static string Hours(double value)
        => value.ToString(HoursFormat, CultureInfo.InvariantCulture);

    private static string Count(int value)
        => value.ToString(CultureInfo.InvariantCulture);
}
