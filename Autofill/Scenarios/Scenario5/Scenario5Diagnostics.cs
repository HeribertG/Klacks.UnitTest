// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario5;

/// <summary>
/// Turns the scenario-5 measurements into the short texts the assertion messages and the diagnosis
/// output are made of. Kept apart from the assertions so a message never has to be read past a line of
/// string building to see what it claims.
/// </summary>
public static class Scenario5Diagnostics
{
    private const string ItemSeparator = ", ";

    private const string None = "none";

    private const string ShareFormat = "0.###";

    private const string HoursFormat = "0.#";

    /// <param name="items">Day-shift share per employee</param>
    public static string DescribeDayShiftShares(IEnumerable<DayShiftShare> items)
        => Join(items.Select(s =>
            $"{s.Employee}{(s.PrefersDayShifts ? "*" : string.Empty)}="
            + $"{s.DayShiftCount.ToString(CultureInfo.InvariantCulture)}/"
            + $"{s.TotalCount.ToString(CultureInfo.InvariantCulture)}"
            + $" ({s.Share.ToString(ShareFormat, CultureInfo.InvariantCulture)})"));

    /// <param name="items">Assignments that stand on a blacklisted shift</param>
    public static string DescribeBlacklistViolations(IEnumerable<PreferenceBlacklistViolation> items)
        => Join(items.Select(v =>
            $"{v.Employee} {v.Date:yyyy-MM-dd} order {v.Order.ToString(CultureInfo.InvariantCulture)} {v.SlotKind}"
            + (v.IsCarryIn ? " (carry-in)" : string.Empty)));

    /// <param name="items">Preferred assignments that take part in a hard finding</param>
    public static string DescribeFalsePositives(IEnumerable<PreferenceFalsePositive> items)
        => Join(items.Select(v =>
            $"{v.Employee} {v.Date:yyyy-MM-dd} order {v.Order.ToString(CultureInfo.InvariantCulture)} {v.SlotKind} "
            + $"breaks {v.HardRuleBroken}"));

    /// <param name="items">Assignments that break a schedule command</param>
    public static string DescribeCommandViolations(IEnumerable<ScheduleCommandViolation> items)
        => Join(items.Select(v =>
            $"{v.Employee} {v.Date:yyyy-MM-dd} {v.Keyword} got order "
            + $"{v.Order.ToString(CultureInfo.InvariantCulture)} {v.AssignedSlotKind} (class {v.AssignedClass})"
            + (v.IsCarryIn ? " (carry-in)" : string.Empty)));

    /// <param name="items">Command windows with what the plan placed inside them</param>
    public static string DescribeWindows(IEnumerable<ScheduleCommandWindow> items)
        => Join(items.Select(w =>
            $"{w.Employee} {w.Keyword} {w.From:MM-dd}..{w.Until:MM-dd} holds "
            + $"{w.AssignmentsInWindow.Count.ToString(CultureInfo.InvariantCulture)} assignment(s)"
            + (w.AssignmentsInWindow.Count == 0
                ? string.Empty
                : " [" + string.Join("/", w.AssignmentsInWindow.Select(a => $"{a.Date:MM-dd} {a.SlotKind}")) + "]")));

    /// <param name="items">Carry-in verdicts including the order dimension</param>
    public static string DescribeCarryIn(IEnumerable<CarryInOrderRespect> items)
        => Join(items.Select(c =>
            $"{c.Employee} expected order {Describe(c.ExpectedOrder)}/{c.ExpectedShiftType} x "
            + $"{c.ExpectedRemainingDays.ToString(CultureInfo.InvariantCulture)}d, got "
            + $"{Describe(c.ActualOrder)}/{Describe(c.ActualFirstSlotKind)} x "
            + $"{c.ActualRemainingDays.ToString(CultureInfo.InvariantCulture)}d"));

    /// <param name="items">Hours per employee</param>
    public static string DescribeHours(IEnumerable<EmployeeHours> items)
        => Join(items.Select(e =>
            $"#{e.ListRank.ToString(CultureInfo.InvariantCulture)} {e.Employee}="
            + $"{e.PlannedHours.ToString(HoursFormat, CultureInfo.InvariantCulture)}h"));

    /// <param name="items">Ranks whose fulfilment rises against the list order</param>
    public static string DescribeMonotonicity(IEnumerable<MonotonicityViolation> items)
        => Join(items.Select(v =>
            $"rank {v.Rank.ToString(CultureInfo.InvariantCulture)} "
            + $"{v.ThisPct.ToString(ShareFormat, CultureInfo.InvariantCulture)} above "
            + v.PrevPct.ToString(ShareFormat, CultureInfo.InvariantCulture)));

    /// <param name="histogram">Free-block histogram of the plan</param>
    public static string DescribeFreeBlocks(IReadOnlyDictionary<int, int> histogram)
        => Join(histogram
            .OrderBy(entry => entry.Key)
            .Select(entry =>
                $"{entry.Key.ToString(CultureInfo.InvariantCulture)}d x "
                + entry.Value.ToString(CultureInfo.InvariantCulture)));

    /// <param name="counts">Per-class shift counts per employee</param>
    public static string DescribeShiftTypeCounts(IEnumerable<EmployeeShiftTypeCounts> counts)
        => Join(counts.Select(c =>
            $"{c.Employee}={c.Early.ToString(CultureInfo.InvariantCulture)}/"
            + $"{c.Late.ToString(CultureInfo.InvariantCulture)}/"
            + c.Night.ToString(CultureInfo.InvariantCulture)));

    /// <param name="diff">Attributed control-group diff of the two runs</param>
    public static string DescribeDiff(AttributedAssignmentDiff diff)
        => $"{diff.ChangedAssignments.Count.ToString(CultureInfo.InvariantCulture)} changed slot(s), "
            + $"{diff.UnexplainedCount.ToString(CultureInfo.InvariantCulture)} unexplained; by cause: "
            + Join(diff.CountByCause.Select(c =>
                $"{c.Cause}={c.Count.ToString(CultureInfo.InvariantCulture)}"));

    /// <param name="items">Changed slots the attribution rule could not explain</param>
    public static string DescribeUnexplained(IEnumerable<AttributedChangedAssignment> items)
        => Join(items.Select(c =>
            $"{c.Date:yyyy-MM-dd} order {c.Order.ToString(CultureInfo.InvariantCulture)} {c.SlotKind}: "
            + $"{c.EmployeeControl ?? "-"} -> {c.EmployeeTreatment ?? "-"}"));

    /// <param name="days">Number of calendar days a FREE window removes from an employee</param>
    /// <param name="periodDays">Length of the planning period in days</param>
    public static string DescribeFreeAdjustedCeiling(int days, int periodDays)
    {
        var reachableDays = periodDays - days;
        var reachableShifts = (int)Math.Floor(reachableDays * Scenario5SpecConstants.IdealFiveTwoWorkShare);
        return $"{days.ToString(CultureInfo.InvariantCulture)} day(s) removed, at most "
            + $"{reachableDays.ToString(CultureInfo.InvariantCulture)} workable days and about "
            + $"{reachableShifts.ToString(CultureInfo.InvariantCulture)} shifts under 5/2";
    }

    private static string Describe(int? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static string Describe(AutofillShiftKind? value)
        => value?.ToString() ?? "-";

    private static string Join(IEnumerable<string> parts)
    {
        var joined = string.Join(ItemSeparator, parts);
        return string.IsNullOrEmpty(joined) ? None : joined;
    }
}
