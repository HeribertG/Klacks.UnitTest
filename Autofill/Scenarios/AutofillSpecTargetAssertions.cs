// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using Klacks.UnitTest.Autofill.Analysis;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios;

/// <summary>
/// The specification targets A4, A5, A7 and A8 of tests/autofill/SPEC.md, judged with the binding values
/// and no tolerance beyond the specification. Shared by scenarios 1, 1b, 2 and 3 so the four judgements are
/// written once; each scenario base declares the [Test] methods and passes what differs between the
/// scenarios (the ranks of A8, the shift kinds of A8, the packages A5 excuses).
/// <para>
/// A7 reads the rotation definition the owner made binding on 2026-10-08
/// (tests/autofill/SPEC-ROTATION-2026-10-08.md, which replaces SPEC.md decision 12b): "forwardRate" is the
/// share of block changes that go to the ideal kind (<see cref="RotationSpecMetrics.IdealTransitionRate"/>),
/// and "every backward or skip transition is justified as forced" means no
/// <see cref="RotationSpecMetrics.Deviations"/> entry without proof. The legacy package
/// <see cref="RotationMetrics.ForwardRate"/> only counts package pairs below 48 h of rest — exactly the pairs
/// the new rule treats as one block — and is 0 when there is none, so it is printed for comparison only.
/// </para>
/// </summary>
public static class AutofillSpecTargetAssertions
{
    private const double ShareComparisonEpsilon = 1e-9;

    private const double PercentFactor = 100;

    private const string PercentFormat = "0.#";

    private const string ShareFormat = "0.###";

    private const string ItemSeparator = "; ";

    private const string Empty = "none";

    /// <summary>A4: the shift kind stays constant inside every package.</summary>
    /// <param name="metrics">Measurement of the judged run</param>
    /// <param name="definition">Scenario of the judged run; supplies the list ranks</param>
    public static void AssertA4(AutofillMetrics metrics, AutofillScenarioDefinition definition)
    {
        var packages = metrics.Packages;
        var mixed = packages.Items.Where(p => p.MixedTypes).Select(p => DescribePackage(p, definition)).ToList();

        packages.MixedTypeCount.ShouldBe(
            0,
            "A4 (specification target, tolerance 0): packages.mixedTypeCount must be 0 — the shift kind stays the same "
            + "inside a package and may only change at a package border — but "
            + $"{Number(packages.MixedTypeCount)} of {Number(packages.Items.Count)} package(s) change kind: "
            + Join(mixed));
    }

    /// <summary>
    /// A5: the package lengths peak at five days as the single most frequent length, at most 15 % (tolerance
    /// 5 percentage points) of the packages are at most two days long, and no package is longer than five.
    /// </summary>
    /// <param name="metrics">Measurement of the judged run</param>
    /// <param name="definition">Scenario of the judged run; supplies the list ranks</param>
    /// <param name="excused">Packages the specification of this scenario takes out of the judgement</param>
    /// <param name="scenarioNote">Scenario-specific reading, quoted in the message</param>
    public static void AssertA5(
        AutofillMetrics metrics,
        AutofillScenarioDefinition definition,
        IReadOnlyCollection<WorkPackage> excused,
        string scenarioNote)
    {
        var judged = metrics.Packages.Items.Where(p => !excused.Contains(p)).ToList();
        var histogram = AutofillPlanAnalyzer.LengthHistogramOf(judged);
        var packages = metrics.Packages with { Items = judged, LengthHistogram = histogram };
        var problems = new List<string>();

        AddModeProblem(problems, judged, histogram);
        AddShortShareProblem(problems, packages, definition);

        var overlong = judged.Where(p => p.LengthDays > AutofillSpecConstants.MaxAllowedPackageLength).ToList();
        if (overlong.Count > 0)
        {
            problems.Add(
                $"{Number(overlong.Count)} package(s) are longer than the maximum of "
                + $"{Number(AutofillSpecConstants.MaxAllowedPackageLength)} days: "
                + Join(overlong.Select(p => DescribePackage(p, definition))));
        }

        problems.ShouldBeEmpty(
            "A5 (specification target): packages.lengthHistogram must have its single mode at "
            + $"{Number(AutofillSpecConstants.ExpectedPackageLengthMode)} days, the share of packages of at most "
            + $"{Number(AutofillSpecConstants.ShortPackageMaxLength)} days must stay below "
            + $"{Percent(AutofillSpecConstants.ShortPackageShareLimit)} % (tolerance "
            + $"{Percent(AutofillSpecConstants.ShortPackageShareTolerance)} percentage points, so a FAIL only above "
            + $"{Percent(AutofillSpecConstants.ShortPackageShareLimit + AutofillSpecConstants.ShortPackageShareTolerance)} %), "
            + $"and no package may exceed {Number(AutofillSpecConstants.MaxAllowedPackageLength)} days. Judged "
            + $"{Number(judged.Count)} of {Number(metrics.Packages.Items.Count)} package(s); excused: "
            + $"{Join(excused.Select(p => DescribePackage(p, definition)))}. Histogram: {DescribeHistogram(histogram)}. "
            + $"{scenarioNote} Problems: {Join(problems)}");
    }

    /// <summary>
    /// A7: at least 80 % of the block changes follow early to late to night to early (unavailable kinds skipped,
    /// restart at early after a long pause), and every other change is provably forced.
    /// </summary>
    /// <param name="metrics">Measurement of the judged run</param>
    /// <param name="definition">Scenario of the judged run; supplies the list ranks</param>
    public static void AssertA7(AutofillMetrics metrics, AutofillScenarioDefinition definition)
    {
        var rotation = metrics.Rotation;
        var spec = rotation.Spec;
        var problems = new List<string>();

        if (spec.TransitionCount == 0)
        {
            problems.Add(
                "no block change owes a rotation, so the rate measures nothing — nothing measured is not a pass");
        }
        else if (spec.IdealTransitionRate + ShareComparisonEpsilon < AutofillSpecConstants.MinForwardRotationRate)
        {
            problems.Add(
                $"only {Number(spec.IdealTransitionCount)} of {Number(spec.TransitionCount)} block change(s) = "
                + $"{Percent(spec.IdealTransitionRate)} % go to the ideal kind, below "
                + $"{Percent(AutofillSpecConstants.MinForwardRotationRate)} %");
        }

        var unforced = spec.Deviations.Where(d => !d.Forced).ToList();
        if (unforced.Count > 0)
        {
            problems.Add(
                $"{Number(unforced.Count)} deviation(s) carry no proof of being forced: "
                + Join(unforced.Select(d => DescribeDeviation(d, definition))));
        }

        problems.ShouldBeEmpty(
            "A7 (specification target): rotation.forwardRate must reach at least "
            + $"{Percent(AutofillSpecConstants.MinForwardRotationRate)} % and every backward or skipping transition must "
            + "be justified as forced. Measured under SPEC-ROTATION-2026-10-08: a block ends at 48 h of rest, the ideal "
            + "successor runs early to late to night to early, a kind the employee may not work on the new block's days "
            + "is skipped, and after 7 free days the cycle restarts at early; forced means a hard rule closed the ideal "
            + "kind (hardRule) or no single swap with a holder of the ideal kind respects the hard rules (coverage). "
            + $"Measured: {Number(spec.IdealTransitionCount)} of {Number(spec.TransitionCount)} ideal, "
            + $"{Number(spec.ForcedDeviationCount)} forced deviation(s): "
            + $"{Join(spec.Deviations.Where(d => d.Forced).Select(d => DescribeDeviation(d, definition)))}; "
            + $"{Number(spec.SingleKindSkipCount)} change(s) owe no rotation (at most one kind allowed), "
            + $"{Number(spec.LongPauseRestartCount)} restart(s) after a long pause. For comparison only, never asserted: "
            + $"the legacy package forwardRate {Share(rotation.ForwardRate)} over {Number(rotation.Transitions.Count)} "
            + "package pair(s) below 48 h of rest, the naive cyclic last-to-first rate "
            + $"{Share(rotation.CyclicLastToFirst.ForwardRate)} over {Number(rotation.CyclicLastToFirst.PairCount)} "
            + $"package pair(s). Problems: {Join(problems)}");
    }

    /// <summary>
    /// A8: per shift kind, the employee holding the most and the one holding the fewest shifts of that kind
    /// differ by at most two, over the asserted ranks.
    /// </summary>
    /// <param name="metrics">Measurement of the judged run</param>
    /// <param name="highestRank">Highest list rank A8 covers in this scenario</param>
    /// <param name="kinds">Shift kinds A8 judges in this scenario</param>
    /// <param name="scenarioNote">Scenario-specific reading, quoted in the message</param>
    public static void AssertA8(
        AutofillMetrics metrics,
        int highestRank,
        IReadOnlyList<AutofillShiftKind> kinds,
        string scenarioNote)
    {
        var counts = metrics.Fairness.ShiftTypeCountPerEmployee.Where(c => c.ListRank <= highestRank).ToList();
        var problems = new List<string>();

        if (counts.Count == 0)
        {
            problems.Add($"the plan reports no employee within ranks 1 to {Number(highestRank)}");
        }

        foreach (var kind in kinds.Where(_ => counts.Count > 0))
        {
            var most = counts.OrderByDescending(c => CountOf(c, kind)).ThenBy(c => c.ListRank).First();
            var fewest = counts.OrderBy(c => CountOf(c, kind)).ThenBy(c => c.ListRank).First();
            var spread = CountOf(most, kind) - CountOf(fewest, kind);
            if (spread > AutofillSpecConstants.MaxShiftKindSpread)
            {
                problems.Add(
                    $"{kind}: {most.Employee} (rank {Number(most.ListRank)}) holds {Number(CountOf(most, kind))}, "
                    + $"{fewest.Employee} (rank {Number(fewest.ListRank)}) holds {Number(CountOf(fewest, kind))} — "
                    + $"spread {Number(spread)}, allowed at most {Number(AutofillSpecConstants.MaxShiftKindSpread)}");
            }
        }

        var spreadOverRanks = AutofillPlanAnalyzer.SpreadOf(counts);
        problems.ShouldBeEmpty(
            $"A8 (specification target): over ranks 1 to {Number(highestRank)} the counts of each judged shift kind "
            + $"({string.Join(", ", kinds)}) may differ by at most {Number(AutofillSpecConstants.MaxShiftKindSpread)} "
            + "shift(s) between the employee holding the most and the one holding the fewest. "
            + $"{scenarioNote} Spread over these ranks: early={Number(spreadOverRanks.Early)}, "
            + $"late={Number(spreadOverRanks.Late)}, night={Number(spreadOverRanks.Night)}. Counts: "
            + $"{Join(counts.Select(DescribeCounts))}. Problems: {Join(problems)}");
    }

    private static void AddModeProblem(
        List<string> problems, IReadOnlyList<WorkPackage> judged, IReadOnlyDictionary<string, int> histogram)
    {
        if (judged.Count == 0)
        {
            problems.Add(
                "the plan contains no package to judge, so it cannot peak at "
                + $"{Number(AutofillSpecConstants.ExpectedPackageLengthMode)} days");
            return;
        }

        var rivals = AutofillPlanAnalyzer.LengthModeRivals(histogram, AutofillSpecConstants.ExpectedPackageLengthMode);
        if (rivals.Count > 0)
        {
            histogram.TryGetValue(Number(AutofillSpecConstants.ExpectedPackageLengthMode), out var atMode);
            problems.Add(
                $"length {Number(AutofillSpecConstants.ExpectedPackageLengthMode)} holds {Number(atMode)} package(s) and "
                + "is not the single most frequent length; at least as frequent: "
                + string.Join(", ", rivals.Select(r => $"{r.Key} days:{Number(r.Value)}")));
        }
    }

    private static void AddShortShareProblem(
        List<string> problems, PackageMetrics packages, AutofillScenarioDefinition definition)
    {
        var limit = AutofillSpecConstants.ShortPackageShareLimit + AutofillSpecConstants.ShortPackageShareTolerance;
        var share = AutofillPlanAnalyzer.ShortPackageShare(packages, AutofillSpecConstants.ShortPackageMaxLength);
        if (share <= limit + ShareComparisonEpsilon)
        {
            return;
        }

        var shortPackages = packages.Items
            .Where(p => p.LengthDays <= AutofillSpecConstants.ShortPackageMaxLength)
            .ToList();
        problems.Add(
            $"{Number(shortPackages.Count)} of {Number(packages.Items.Count)} package(s) = {Percent(share)} % are at most "
            + $"{Number(AutofillSpecConstants.ShortPackageMaxLength)} days long, above {Percent(limit)} %: "
            + Join(shortPackages.Select(p => DescribePackage(p, definition))));
    }

    private static int CountOf(EmployeeShiftTypeCounts counts, AutofillShiftKind kind) => kind switch
    {
        AutofillShiftKind.Early => counts.Early,
        AutofillShiftKind.Late => counts.Late,
        AutofillShiftKind.Night => counts.Night,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A8 judges early, late and night only."),
    };

    private static string DescribePackage(WorkPackage package, AutofillScenarioDefinition definition)
        => $"{package.Employee} (rank {Number(definition.ListRankOf(package.Employee))}) "
            + $"{Date(package.StartDate)}..{Date(package.EndDate)} ({Number(package.LengthDays)} d, "
            + (package.MixedTypes
                ? $"starts {package.ShiftType}, ends {package.LastShiftType})"
                : $"{package.ShiftType})");

    private static string DescribeDeviation(RotationSpecDeviation deviation, AutofillScenarioDefinition definition)
        => $"{deviation.Employee} (rank {Number(definition.ListRankOf(deviation.Employee))}) block from "
            + $"{Date(deviation.Date)}: {deviation.From} to {deviation.To}, ideal {deviation.Ideal} ({deviation.Cause})";

    private static string DescribeCounts(EmployeeShiftTypeCounts counts)
        => $"rank {Number(counts.ListRank)} {counts.Employee} E={Number(counts.Early)} L={Number(counts.Late)} "
            + $"N={Number(counts.Night)}";

    private static string DescribeHistogram(IReadOnlyDictionary<string, int> histogram)
        => string.Join(", ", histogram.Select(entry => $"{entry.Key}:{Number(entry.Value)}"));

    private static string Join(IEnumerable<string> parts)
    {
        var materialised = parts.ToList();
        return materialised.Count == 0 ? Empty : string.Join(ItemSeparator, materialised);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Percent(double share) => (share * PercentFactor).ToString(PercentFormat, CultureInfo.InvariantCulture);

    private static string Share(double value) => value.ToString(ShareFormat, CultureInfo.InvariantCulture);

    private static string Date(DateOnly date) => date.ToString(AutofillSpecConstants.IsoDateFormat, CultureInfo.InvariantCulture);
}
