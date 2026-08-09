// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Analysis.Model;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario4;

/// <summary>
/// Compares two scenario-4 measurements LABEL-INVARIANTLY: everything that is not tied to a particular
/// order has to match value for value, and everything that is per order is compared as a sorted
/// multiset, so which order carries which numbers may differ while the set of numbers may not.
/// <para>
/// Why not a plain diff. The symmetry run gives the first and the third order each other's shift ids.
/// Employee identity survives that — the carry-in stays bound to its labels — but the numbers of an
/// order travel with the ids, so a value-for-value comparison of per-order metrics would report a
/// difference that is a relabelling and nothing else. Comparing multisets removes exactly that and
/// nothing more.
/// </para>
/// <para>
/// One caveat this comparison cannot remove, and the reader has to know it: the ids are also the
/// auction's sort key, because three identically cut orders share date and start time. Swapping them
/// therefore changes which order is auctioned first, which is a different starting point for the same
/// problem and not a pure relabelling. A difference found here is a finding about the algorithm's
/// sensitivity to the slot order, not automatically a defect.
/// </para>
/// </summary>
public static class Scenario4SymmetryComparison
{
    private const string NumberFormat = "0.######";

    private const string ListSeparator = "|";

    /// <summary>
    /// Returns one entry per metric the two runs disagree on; an empty result means the two runs are
    /// the same run up to the order labels.
    /// </summary>
    /// <param name="left">Measurement of the reference run</param>
    /// <param name="right">Measurement of the run that must match it</param>
    public static IReadOnlyList<Scenario4MetricDifference> Compare(AutofillMetrics left, AutofillMetrics right)
    {
        var differences = new List<Scenario4MetricDifference>();

        CompareValue(differences, "coverage.totalRequiredShifts", left.Coverage.TotalRequiredShifts, right.Coverage.TotalRequiredShifts);
        CompareValue(differences, "coverage.filledShifts", left.Coverage.FilledShifts, right.Coverage.FilledShifts);
        CompareValue(differences, "coverage.unfilledShifts.count", left.Coverage.UnfilledShifts.Count, right.Coverage.UnfilledShifts.Count);
        CompareValue(differences, "coverage.oversuppliedSlots", left.Coverage.OversuppliedSlots, right.Coverage.OversuppliedSlots);
        CompareValue(differences, "coverage.doubleBookings.count", left.Coverage.DoubleBookings.Count, right.Coverage.DoubleBookings.Count);
        CompareValue(
            differences,
            "coverage.crossOrderDoubleBookings.count",
            left.Coverage.CrossOrderDoubleBookings.Count,
            right.Coverage.CrossOrderDoubleBookings.Count);
        CompareMultiset(
            differences,
            "coverage.perOrder",
            left.Coverage.PerOrder.Select(DescribeOrderCoverage),
            right.Coverage.PerOrder.Select(DescribeOrderCoverage));
        CompareSequence(
            differences,
            "coverage.assignmentsPerDay",
            left.Coverage.AssignmentsPerDay.Select(d => Number(d.Count)),
            right.Coverage.AssignmentsPerDay.Select(d => Number(d.Count)));

        CompareValue(differences, "legality.restViolations.count", left.Legality.RestViolations.Count, right.Legality.RestViolations.Count);
        CompareValue(
            differences,
            "legality.nightToEarlyViolations.count",
            left.Legality.NightToEarlyViolations.Count,
            right.Legality.NightToEarlyViolations.Count);
        CompareValue(
            differences,
            "legality.restViolationsCrossOrder.count",
            left.Legality.RestViolationsCrossOrder.Count,
            right.Legality.RestViolationsCrossOrder.Count);

        CompareValue(differences, "packages.items.count", left.Packages.Items.Count, right.Packages.Items.Count);
        CompareValue(differences, "packages.mixedTypeCount", left.Packages.MixedTypeCount, right.Packages.MixedTypeCount);
        CompareValue(differences, "packages.idealShare", left.Packages.IdealShare, right.Packages.IdealShare);
        CompareSequence(
            differences,
            "packages.lengthHistogram",
            left.Packages.LengthHistogram.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Key + ":" + Number(e.Value)),
            right.Packages.LengthHistogram.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Key + ":" + Number(e.Value)));
        CompareSequence(
            differences,
            "packages.freeBlockHistogram",
            left.Packages.FreeBlockHistogram.OrderBy(e => e.Key).Select(e => Number(e.Key) + ":" + Number(e.Value)),
            right.Packages.FreeBlockHistogram.OrderBy(e => e.Key).Select(e => Number(e.Key) + ":" + Number(e.Value)));
        CompareValue(
            differences,
            "packages.forcedExtraFreeDays.count",
            left.Packages.ForcedExtraFreeDays.Count,
            right.Packages.ForcedExtraFreeDays.Count);

        CompareValue(differences, "rotation.forwardRate", left.Rotation.ForwardRate, right.Rotation.ForwardRate);
        CompareValue(differences, "rotation.transitions.count", left.Rotation.Transitions.Count, right.Rotation.Transitions.Count);
        CompareValue(
            differences,
            "rotation.backwardOrSkipCount",
            left.Rotation.BackwardOrSkipCount,
            right.Rotation.BackwardOrSkipCount);

        CompareSequence(
            differences,
            "hours.fulfillmentByRank",
            left.Hours.FulfillmentByRank.Select(Number),
            right.Hours.FulfillmentByRank.Select(Number));
        CompareSequence(
            differences,
            "hours.zeroHourEmployees",
            left.Hours.ZeroHourEmployees,
            right.Hours.ZeroHourEmployees);
        CompareValue(
            differences,
            "hours.monotonicityViolations.count",
            left.Hours.MonotonicityViolations.Count,
            right.Hours.MonotonicityViolations.Count);

        CompareSequence(
            differences,
            "fairness.shiftTypeCountPerEmployee",
            left.Fairness.ShiftTypeCountPerEmployee.Select(DescribeCounts),
            right.Fairness.ShiftTypeCountPerEmployee.Select(DescribeCounts));
        CompareValue(differences, "fairness.spreadPerType.early", left.Fairness.SpreadPerType.Early, right.Fairness.SpreadPerType.Early);
        CompareValue(differences, "fairness.spreadPerType.late", left.Fairness.SpreadPerType.Late, right.Fairness.SpreadPerType.Late);
        CompareValue(differences, "fairness.spreadPerType.night", left.Fairness.SpreadPerType.Night, right.Fairness.SpreadPerType.Night);

        CompareValue(
            differences,
            "orders.switchesWithinPackage.count",
            left.Orders.SwitchesWithinPackage.Count,
            right.Orders.SwitchesWithinPackage.Count);
        CompareSequence(
            differences,
            "orders.switchesPerEmployee",
            left.Orders.SwitchesPerEmployee.Select(s => s.Employee + ":" + Number(s.SwitchCount)),
            right.Orders.SwitchesPerEmployee.Select(s => s.Employee + ":" + Number(s.SwitchCount)));
        CompareMultiset(
            differences,
            "orders.employeeDistribution",
            left.Orders.EmployeeDistribution.Select(DescribeDistribution),
            right.Orders.EmployeeDistribution.Select(DescribeDistribution));

        CompareSequence(
            differences,
            "carryIn.threeDimensional.ok",
            left.CarryInThreeDimensional.Select(c => c.Employee + ":" + c.Ok),
            right.CarryInThreeDimensional.Select(c => c.Employee + ":" + c.Ok));

        return differences;
    }

    private static string DescribeOrderCoverage(OrderCoverage coverage)
        => Number(coverage.RequiredShifts) + "/" + Number(coverage.FilledShifts) + "/" + Number(coverage.Unfilled.Count);

    private static string DescribeDistribution(OrderEmployeeDistribution distribution)
        => Number(distribution.Employees.Count) + "/" + Number(distribution.MeanListRank);

    private static string DescribeCounts(EmployeeShiftTypeCounts counts)
        => counts.Employee + ":" + Number(counts.Early) + "/" + Number(counts.Late) + "/" + Number(counts.Night);

    private static void CompareValue(
        List<Scenario4MetricDifference> differences, string metric, int left, int right)
    {
        if (left != right)
        {
            differences.Add(new Scenario4MetricDifference(metric, Number(left), Number(right)));
        }
    }

    private static void CompareValue(
        List<Scenario4MetricDifference> differences, string metric, double left, double right)
    {
        var leftText = Number(left);
        var rightText = Number(right);
        if (!string.Equals(leftText, rightText, StringComparison.Ordinal))
        {
            differences.Add(new Scenario4MetricDifference(metric, leftText, rightText));
        }
    }

    private static void CompareSequence(
        List<Scenario4MetricDifference> differences,
        string metric,
        IEnumerable<string> left,
        IEnumerable<string> right)
    {
        var leftText = string.Join(ListSeparator, left);
        var rightText = string.Join(ListSeparator, right);
        if (!string.Equals(leftText, rightText, StringComparison.Ordinal))
        {
            differences.Add(new Scenario4MetricDifference(metric, leftText, rightText));
        }
    }

    private static void CompareMultiset(
        List<Scenario4MetricDifference> differences,
        string metric,
        IEnumerable<string> left,
        IEnumerable<string> right)
        => CompareSequence(
            differences,
            metric,
            left.OrderBy(v => v, StringComparer.Ordinal),
            right.OrderBy(v => v, StringComparer.Ordinal));

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(double value) => value.ToString(NumberFormat, CultureInfo.InvariantCulture);
}
