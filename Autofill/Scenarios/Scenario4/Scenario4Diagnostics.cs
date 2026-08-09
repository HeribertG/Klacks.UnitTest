// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Analysis.Model;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario4;

/// <summary>
/// Turns the scenario-4 measurements into the short texts the assertion messages and the diagnosis
/// output are made of. Kept apart from the assertions so a message never has to be read past a line
/// of string building to see what it claims.
/// </summary>
public static class Scenario4Diagnostics
{
    private const string ItemSeparator = ", ";

    private const string None = "none";

    private const string PercentFormat = "0.#";

    private const string HoursFormat = "0.#";

    private const string RatioFormat = "0.###";

    /// <param name="items">Per-order coverage of one plan</param>
    public static string DescribeOrderCoverage(IEnumerable<OrderCoverage> items)
        => Join(items.Select(o =>
            $"order {o.Order.ToString(CultureInfo.InvariantCulture)}: "
            + $"{o.FilledShifts.ToString(CultureInfo.InvariantCulture)}/"
            + $"{o.RequiredShifts.ToString(CultureInfo.InvariantCulture)}"));

    /// <param name="items">Days whose assignment count deviates from the demanded one</param>
    public static string DescribeDayCounts(IEnumerable<DayAssignmentCount> items)
        => Join(items.Select(d => $"{d.Date:yyyy-MM-dd}={d.Count.ToString(CultureInfo.InvariantCulture)}"));

    /// <param name="items">Days on which an employee holds shifts of several orders</param>
    public static string DescribeCrossOrderDays(IEnumerable<CrossOrderDoubleBooking> items)
        => Join(items.Select(c =>
            $"{c.Employee} {c.Date:yyyy-MM-dd} orders "
            + string.Join("+", c.Orders.Select(o => o.ToString(CultureInfo.InvariantCulture)))));

    /// <param name="items">Rest-time violations whose two shifts belong to different orders</param>
    public static string DescribeCrossOrderRest(IEnumerable<CrossOrderRestViolation> items)
        => Join(items.Select(v =>
            $"{v.Employee} {v.DateFrom:MM-dd} order {v.FromOrder.ToString(CultureInfo.InvariantCulture)} {v.FromShift}"
            + $" -> {v.DateTo:MM-dd} order {v.ToOrder.ToString(CultureInfo.InvariantCulture)} {v.ToShift}"
            + $" gap {v.GapHours.ToString(HoursFormat, CultureInfo.InvariantCulture)} h"));

    /// <param name="items">Packages that touch more than one order</param>
    public static string DescribeOrderSwitchesInPackage(IEnumerable<OrderSwitchInPackage> items)
        => Join(items.Select(s =>
            $"{s.Employee} from {s.PackageStart:yyyy-MM-dd}: "
            + string.Join("-", s.Orders.Select(o => o.ToString(CultureInfo.InvariantCulture)))));

    /// <param name="items">Order changes per employee</param>
    public static string DescribeOrderSwitchesPerEmployee(IEnumerable<OrderSwitchesPerEmployee> items)
        => Join(items.Select(s => $"{s.Employee}={s.SwitchCount.ToString(CultureInfo.InvariantCulture)}"));

    /// <param name="items">Which employees staff which order</param>
    public static string DescribeOrderDistribution(IEnumerable<OrderEmployeeDistribution> items)
        => Join(items.Select(d =>
            $"order {d.Order.ToString(CultureInfo.InvariantCulture)}: "
            + $"{d.Employees.Count.ToString(CultureInfo.InvariantCulture)} employees, mean rank "
            + d.MeanListRank.ToString(RatioFormat, CultureInfo.InvariantCulture)));

    /// <param name="items">Three-dimensional continuation measurements</param>
    public static string DescribeCarryInThreeDimensional(IEnumerable<CarryInOrderRespect> items)
        => Join(items.Select(c =>
            $"{c.Employee}: expected order {Describe(c.ExpectedOrder)} {c.ExpectedShiftType} x "
            + $"{c.ExpectedRemainingDays.ToString(CultureInfo.InvariantCulture)}, actual order "
            + $"{Describe(c.ActualOrder)} {c.ActualShiftType?.ToString() ?? None} x "
            + $"{c.ActualRemainingDays.ToString(CultureInfo.InvariantCulture)}"));

    /// <param name="items">Leading free-day runs the plan offered no way to fill</param>
    public static string DescribeForcedFreeDays(IEnumerable<ForcedFreeDayRun> items)
        => Join(items.Select(f => $"{f.Employee} {f.DateFrom:MM-dd}..{f.DateTo:MM-dd} ({f.Cause})"));

    /// <param name="items">Hours per employee</param>
    public static string DescribeHours(IEnumerable<EmployeeHours> items)
        => Join(items.Select(h =>
            $"rank {h.ListRank.ToString(CultureInfo.InvariantCulture)} {h.Employee}: "
            + $"{h.PlannedHours.ToString(HoursFormat, CultureInfo.InvariantCulture)} h ("
            + $"{(h.FulfillmentPct * 100).ToString(PercentFormat, CultureInfo.InvariantCulture)} %)"));

    /// <param name="items">Ranks whose fulfilment rises against the list order</param>
    public static string DescribeMonotonicity(IEnumerable<MonotonicityViolation> items)
        => Join(items.Select(m =>
            $"rank {m.Rank.ToString(CultureInfo.InvariantCulture)}: "
            + $"{(m.PrevPct * 100).ToString(PercentFormat, CultureInfo.InvariantCulture)} % -> "
            + $"{(m.ThisPct * 100).ToString(PercentFormat, CultureInfo.InvariantCulture)} %"));

    /// <param name="items">Shift-kind counts per employee</param>
    public static string DescribeShiftCounts(IEnumerable<EmployeeShiftTypeCounts> items)
        => Join(items.Select(c =>
            $"{c.Employee} E{c.Early.ToString(CultureInfo.InvariantCulture)}/"
            + $"L{c.Late.ToString(CultureInfo.InvariantCulture)}/"
            + $"N{c.Night.ToString(CultureInfo.InvariantCulture)}"));

    /// <param name="items">Package length histogram</param>
    public static string DescribeHistogram(IEnumerable<KeyValuePair<string, int>> items)
        => Join(items.Select(e => $"{e.Key}:{e.Value.ToString(CultureInfo.InvariantCulture)}"));

    /// <param name="items">Free block histogram</param>
    public static string DescribeFreeBlocks(IEnumerable<KeyValuePair<int, int>> items)
        => Join(items.Select(e =>
            $"{e.Key.ToString(CultureInfo.InvariantCulture)}:{e.Value.ToString(CultureInfo.InvariantCulture)}"));

    /// <param name="items">Metrics that differ between two runs that must be identical</param>
    public static string DescribeMetricDifferences(IEnumerable<Scenario4MetricDifference> items)
        => Join(items.Select(d => $"{d.Metric}: {d.ValueLeft} vs {d.ValueRight}"));

    /// <summary>Median of a set of counts; 0 for an empty set.</summary>
    /// <param name="values">Counts to take the median of</param>
    public static double MedianOf(IReadOnlyList<int> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(v => v).ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    private static string Describe(int? order)
        => order?.ToString(CultureInfo.InvariantCulture) ?? "any";

    private static string Join(IEnumerable<string> parts)
    {
        var materialised = parts.ToList();
        return materialised.Count == 0 ? None : string.Join(ItemSeparator, materialised);
    }
}
