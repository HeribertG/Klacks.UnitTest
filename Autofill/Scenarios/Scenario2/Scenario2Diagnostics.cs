// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Analysis.Model;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario2;

/// <summary>
/// Turns measured detail into the one-line texts the assertion messages carry. A failing rule
/// assertion has to say which employee, which day and which value it saw, otherwise the run produces
/// a verdict nobody can act on; keeping the formatting here stops that detail from drowning the
/// assertions themselves.
/// </summary>
public static class Scenario2Diagnostics
{
    private const string Empty = "none";

    private const string ItemSeparator = "; ";

    private const string NumberFormat = "0.###";

    /// <param name="items">Slots nobody received</param>
    public static string DescribeUnfilled(IEnumerable<UnfilledShift> items)
        => Join(items.Select(i => $"{i.Date:yyyy-MM-dd} {i.ShiftType}"));

    /// <param name="items">Assignment pairs that conflict: the same shift twice, or overlapping times</param>
    public static string DescribeDoubleBookings(IEnumerable<DoubleBooking> items)
        => Join(items.Select(i =>
            $"{i.Employee} on {i.Date:yyyy-MM-dd}: {string.Join(" + ", i.Shifts)} ({i.Reason})"));

    /// <param name="items">Shift pairs below the contractual rest time</param>
    public static string DescribeRestViolations(IEnumerable<RestTimeViolation> items)
        => Join(items.Select(i =>
            $"{i.Employee} {i.FromShift} {i.DateFrom:yyyy-MM-dd} to {i.ToShift} {i.DateTo:yyyy-MM-dd} "
            + $"= {Number(i.GapHours)} h"));

    /// <param name="items">Night shifts directly followed by an early shift</param>
    public static string DescribeNightToEarly(IEnumerable<NightToEarlyViolation> items)
        => Join(items.Select(i => $"{i.Employee} night on {i.Date:yyyy-MM-dd} then early on {i.Date.AddDays(1):yyyy-MM-dd}"));

    /// <param name="items">Work packages to list</param>
    public static string DescribePackages(IEnumerable<WorkPackage> items)
        => Join(items.Select(PackageLine));

    /// <param name="package">Work package, or null when the employee received none</param>
    public static string PackageLine(WorkPackage? package)
        => package is null
            ? "no in-period package at all"
            : $"{package.Employee} {package.StartDate:MM-dd}..{package.EndDate:MM-dd} "
                + $"({package.LengthDays.ToString(CultureInfo.InvariantCulture)} d, {package.ShiftType}"
                + (package.MixedTypes ? ", mixed kinds)" : ")");

    /// <param name="histogram">Package count per length bucket</param>
    public static string DescribeHistogram(IReadOnlyDictionary<string, int> histogram)
        => Join(histogram.Select(entry => $"{entry.Key}:{entry.Value.ToString(CultureInfo.InvariantCulture)}"));

    /// <param name="items">Per-employee hour accounts</param>
    public static string DescribeHours(IEnumerable<EmployeeHours> items)
        => Join(items.Select(i =>
            $"rank {i.ListRank.ToString(CultureInfo.InvariantCulture)} {i.Employee} {Number(i.PlannedHours)} h "
            + $"of {Number(i.GuaranteedHours)} h = {Number(i.FulfillmentPct * 100)} %"));

    /// <param name="items">Ranks whose fulfilment rises against the list order</param>
    public static string DescribeMonotonicity(IEnumerable<MonotonicityViolation> items)
        => Join(items.Select(i =>
            $"rank {i.Rank.ToString(CultureInfo.InvariantCulture)} reaches {Number(i.ThisPct * 100)} % "
            + $"above the rank before it at {Number(i.PrevPct * 100)} %"));

    /// <param name="items">Package-to-package shift-kind changes</param>
    public static string DescribeTransitions(IEnumerable<RotationTransition> items)
        => Join(items.Select(i => $"{i.Employee} {i.FromType} to {i.ToType}"));

    /// <param name="items">Per-employee shift-kind counts</param>
    public static string DescribeShiftCounts(IEnumerable<EmployeeShiftTypeCounts> items)
        => Join(items.Select(i =>
            $"rank {i.ListRank.ToString(CultureInfo.InvariantCulture)} {i.Employee} "
            + $"E={i.Early.ToString(CultureInfo.InvariantCulture)} "
            + $"L={i.Late.ToString(CultureInfo.InvariantCulture)} "
            + $"N={i.Night.ToString(CultureInfo.InvariantCulture)}"));

    /// <param name="items">Carry-in continuation results</param>
    public static string DescribeCarryIn(IEnumerable<CarryInRespect> items)
        => Join(items.Select(i =>
            $"{i.Employee} first kind expected {i.ExpectedShiftType} but was "
            + $"{(i.ActualFirstShiftType?.ToString() ?? "no in-period shift")}, remaining days expected "
            + $"{i.ExpectedRemainingDays.ToString(CultureInfo.InvariantCulture)} but were "
            + $"{i.ActualRemainingDays.ToString(CultureInfo.InvariantCulture)}, package across the boundary "
            + $"{i.ActualPackageDays.ToString(CultureInfo.InvariantCulture)} d"));

    /// <summary>
    /// Packages of one employee, ordered by start date. Packages span the month boundary, so the
    /// first one may start in February; packages closed before the period are not listed.
    /// </summary>
    /// <param name="metrics">Measurement of the run</param>
    /// <param name="employee">Employee identifier</param>
    public static IReadOnlyList<WorkPackage> PackagesOf(AutofillMetrics metrics, string employee)
        => metrics.Packages.Items
            .Where(p => string.Equals(p.Employee, employee, StringComparison.Ordinal))
            .OrderBy(p => p.StartDate)
            .ToList();

    /// <param name="metrics">Measurement of the run</param>
    /// <param name="employee">Employee identifier</param>
    public static WorkPackage? FirstPackageOf(AutofillMetrics metrics, string employee)
        => PackagesOf(metrics, employee).FirstOrDefault();

    /// <summary>
    /// The same carry-in measurement taken on the auction seed plan, i.e. before any evolution ran.
    /// Cited by A10 to A13 so a failure can be placed: an identical failure here means the engine
    /// never constructed the continuation, a failure only in the final plan means it built it and
    /// evolution dissolved it again.
    /// </summary>
    /// <param name="seedMetrics">Measurement of the auction seed plan</param>
    /// <param name="employees">Employees to report</param>
    public static string DescribeSeedCarryIn(AutofillMetrics seedMetrics, IEnumerable<string> employees)
    {
        var wanted = employees.ToHashSet(StringComparer.Ordinal);
        return DescribeCarryIn(seedMetrics.CarryIn.Where(c => wanted.Contains(c.Employee)));
    }

    private static string Number(double value)
        => value.ToString(NumberFormat, CultureInfo.InvariantCulture);

    private static string Join(IEnumerable<string> parts)
    {
        var materialised = parts.ToList();
        return materialised.Count == 0 ? Empty : string.Join(ItemSeparator, materialised);
    }
}
