// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Analysis;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Scenarios;

/// <summary>
/// The eight numbers the no-regression floor is built from, read off one measured plan. One instance
/// describes one engine run; the band of a scenario is the span these eight numbers cover over the
/// seeds the baseline was measured with.
/// </summary>
/// <param name="ForwardRate">rotation.forwardRate — higher is better</param>
/// <param name="MixedTypeCount">packages.mixedTypeCount — lower is better</param>
/// <param name="ShortPackageShare">Share of packages of at most two days — lower is better</param>
/// <param name="PackagesOverIdealLength">Packages longer than the five-day ideal — lower is better</param>
/// <param name="IdealShare">packages.idealShare — higher is better</param>
/// <param name="ShiftKindSpread">Widest of the three fairness.spreadPerType values — lower is better</param>
/// <param name="MonotonicityViolations">hours.monotonicityViolations — lower is better</param>
/// <param name="TopRanksPlannedHours">
/// Planned hours of list ranks 1 to <see cref="AutofillSpecConstants.TopRankUpperBound"/> added up —
/// higher is better
/// </param>
/// <param name="CarryInOkCount">
/// Continued carry-in packages — higher is better; null in a scenario without a previous month
/// </param>
public sealed record AutofillBandValues(
    double ForwardRate,
    int MixedTypeCount,
    double ShortPackageShare,
    int PackagesOverIdealLength,
    double IdealShare,
    int ShiftKindSpread,
    int MonotonicityViolations,
    double TopRanksPlannedHours,
    int? CarryInOkCount)
{
    /// <summary>Reads the eight numbers off a measurement, exactly as the guards read them.</summary>
    /// <param name="metrics">Measurement of one engine run</param>
    /// <param name="hasCarryIn">True when the scenario carries a previous month worth reporting</param>
    public static AutofillBandValues From(AutofillMetrics metrics, bool hasCarryIn)
    {
        var spread = metrics.Fairness.SpreadPerType;

        return new AutofillBandValues(
            ForwardRate: metrics.Rotation.ForwardRate,
            MixedTypeCount: metrics.Packages.MixedTypeCount,
            ShortPackageShare: AutofillPlanAnalyzer.ShortPackageShare(
                metrics.Packages, AutofillSpecConstants.ShortPackageMaxLength),
            PackagesOverIdealLength: AutofillPlanAnalyzer.PackagesLongerThan(
                metrics.Packages, AutofillSpecConstants.MaxAllowedPackageLength),
            IdealShare: metrics.Packages.IdealShare,
            ShiftKindSpread: Math.Max(spread.Early, Math.Max(spread.Late, spread.Night)),
            MonotonicityViolations: metrics.Hours.MonotonicityViolations.Count,
            TopRanksPlannedHours: TopRanksPlannedHoursOf(metrics),
            CarryInOkCount: hasCarryIn ? metrics.CarryIn.Count(c => c.Ok) : null);
    }

    /// <summary>
    /// Hours the plan gives the top ranks together. Rule 5 of the binding priority table wants the
    /// guaranteed hours served strictly top-down and outranks rule 6, package integrity, so hours
    /// drifting from the top ranks down to the last one is a regression at a high priority even when it
    /// leaves the total untouched — which is exactly what a per-rank fulfilment share or a spread
    /// cannot show. The bound is the same in every scenario, variant 1b included, so the three
    /// scenarios stay comparable.
    /// </summary>
    /// <param name="metrics">Measurement of one engine run</param>
    public static double TopRanksPlannedHoursOf(AutofillMetrics metrics)
        => metrics.Hours.PerEmployee
            .Where(e => e.ListRank <= AutofillSpecConstants.TopRankUpperBound)
            .Sum(e => e.PlannedHours);

    /// <summary>
    /// The worst of a set of measurements, metric by metric: the lowest value where higher is better
    /// and the highest where lower is better. This is the value a band is pinned to — a guard reading
    /// it only turns red once the run is worse than everything the current engine produced.
    /// </summary>
    /// <param name="values">Measurements to fold; must not be empty</param>
    public static AutofillBandValues WorstOf(IReadOnlyList<AutofillBandValues> values)
        => Fold(values, worst: true);

    /// <summary>The best of a set of measurements, metric by metric; the other end of the band.</summary>
    /// <param name="values">Measurements to fold; must not be empty</param>
    public static AutofillBandValues BestOf(IReadOnlyList<AutofillBandValues> values)
        => Fold(values, worst: false);

    private static AutofillBandValues Fold(IReadOnlyList<AutofillBandValues> values, bool worst)
    {
        if (values.Count == 0)
        {
            throw new ArgumentException("A band needs at least one measurement.", nameof(values));
        }

        var carryIn = values.Where(v => v.CarryInOkCount is not null).Select(v => v.CarryInOkCount!.Value).ToList();

        return new AutofillBandValues(
            ForwardRate: worst ? values.Min(v => v.ForwardRate) : values.Max(v => v.ForwardRate),
            MixedTypeCount: worst ? values.Max(v => v.MixedTypeCount) : values.Min(v => v.MixedTypeCount),
            ShortPackageShare: worst ? values.Max(v => v.ShortPackageShare) : values.Min(v => v.ShortPackageShare),
            PackagesOverIdealLength: worst
                ? values.Max(v => v.PackagesOverIdealLength)
                : values.Min(v => v.PackagesOverIdealLength),
            IdealShare: worst ? values.Min(v => v.IdealShare) : values.Max(v => v.IdealShare),
            ShiftKindSpread: worst ? values.Max(v => v.ShiftKindSpread) : values.Min(v => v.ShiftKindSpread),
            MonotonicityViolations: worst
                ? values.Max(v => v.MonotonicityViolations)
                : values.Min(v => v.MonotonicityViolations),
            TopRanksPlannedHours: worst
                ? values.Min(v => v.TopRanksPlannedHours)
                : values.Max(v => v.TopRanksPlannedHours),
            CarryInOkCount: carryIn.Count == 0 ? null : worst ? carryIn.Min() : carryIn.Max());
    }
}
