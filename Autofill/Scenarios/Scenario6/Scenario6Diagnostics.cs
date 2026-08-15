// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario6;

/// <summary>
/// Renders the scenario-6 measurements as the report reads them. Every method answers with the
/// measured values and never with a verdict: an assertion message says what was expected, and these
/// lines say what was found, so the two can be read against each other without running anything again.
/// </summary>
public static class Scenario6Diagnostics
{
    private const string Separator = ", ";

    private const string Empty = "none";

    private const int MaxListed = 12;

    /// <summary>Absence windows with their row counts and credit.</summary>
    /// <param name="entries">Window entries of the run</param>
    public static string DescribeWindows(IReadOnlyList<AbsenceWindowEntry> entries)
        => entries.Count == 0
            ? Empty
            : string.Join(Separator, entries.Select(e =>
                $"{e.Employee} {e.FromInclusive:MM-dd}..{e.UntilInclusive:MM-dd} "
                + $"{Count(e.Rows)}d/{Count(e.PaidRows)} paid/{Hours(e.CreditHours)} h"));

    /// <summary>Assignments that stand on an absence day.</summary>
    /// <param name="violations">Violations of the run</param>
    public static string DescribeViolations(IReadOnlyList<AbsenceViolation> violations)
        => violations.Count == 0
            ? Empty
            : string.Join(Separator, violations.Take(MaxListed).Select(v =>
                $"{v.Employee} {v.Date:MM-dd} {v.SlotKind}/order {Count(v.Order)}"
                + (v.IsCarryIn ? " (carry-in)" : string.Empty)));

    /// <summary>Assignments overlapping an absence window, with the end that lies inside it.</summary>
    /// <param name="cases">Boundary cases of the run</param>
    public static string DescribeBoundaryCases(IReadOnlyList<AbsenceBoundaryCase> cases)
        => cases.Count == 0
            ? Empty
            : string.Join(Separator, cases.Take(MaxListed).Select(c =>
                $"{c.Employee} {c.Date:MM-dd} {c.SlotKind} ends {c.EndsAt:MM-dd HH:mm}, window "
                + $"{c.WindowFrom:MM-dd}..{c.WindowUntil:MM-dd}, evaluated against {c.EvaluatedAgainst}"));

    /// <summary>The hour target of every absent employee with the credit counted in.</summary>
    /// <param name="entries">Credit-target entries of the run</param>
    public static string DescribeCreditTarget(IReadOnlyList<CreditTargetEntry> entries)
        => entries.Count == 0
            ? Empty
            : string.Join(Separator, entries.Select(e =>
                $"{e.Employee} planned {Hours(e.PlannedHours)} + credit {Hours(e.CreditHours)} "
                + $"= {Hours(e.CoveredIncludingCredit)} of {Hours(e.OriginalTarget)} h"
                + (e.Fulfilled ? " (met)" : $" (short {Hours(e.Shortfall)} h"
                    + (e.ForcedShortfallDeclared ? $", forced, cause={e.ShortfallCause})" : ", NOT declared)"))));

    /// <summary>The capacity arithmetic of the fixture.</summary>
    /// <param name="availability">Availability metrics of the run</param>
    public static string DescribeAvailability(AvailabilityMetrics availability)
        => $"places {Count(availability.SlotsTotal)}, demand {Count(availability.RequiredAssignments)}, work share "
            + $"{Percent(availability.WorkRatioRequired)} against the 5/2 ceiling of "
            + $"{Percent(availability.MaxRatioUnder52)}, forced extra shifts "
            + $"{Count(availability.ForcedExtraShifts)}, window places {Count(availability.AbsenceWindowSlots)} for "
            + $"{Count(availability.AbsenceWindowRequired)} assignments = "
            + $"{Percent(availability.AbsenceWindowWorkRatio)}, tightest day "
            + DescribeTightestDay(availability.TightestDay);

    /// <summary>The day the fewest employees are available on.</summary>
    /// <param name="day">Tightest day of the run, or null when none was computed</param>
    public static string DescribeTightestDay(TightestDay? day)
        => day is null
            ? Empty
            : $"{day.Date:yyyy-MM-dd} with {Count(day.AvailableEmployees)} available for "
                + $"{Count(day.RequiredAssignments)} assignments = {Percent(day.Ratio)}";

    /// <summary>What the shift class did across each absence window.</summary>
    /// <param name="entries">Continuity entries of the run</param>
    public static string DescribeContinuity(IReadOnlyList<ContinuityAcrossAbsence> entries)
        => entries.Count == 0
            ? Empty
            : string.Join(Separator, entries.Select(e =>
                $"{e.Employee} {Kind(e.KindBefore)} -> {Kind(e.KindAfter)} over {Count(e.GapDays)} days"
                + (e.ContinuesForward ? " (forward)" : e.RestartsOnEarly ? " (restart on early)" : " (other)")));

    /// <summary>Packages an absence cut short.</summary>
    /// <param name="cuts">Absence cuts of the run</param>
    public static string DescribeCuts(IReadOnlyList<AbsenceCut> cuts)
        => cuts.Count == 0
            ? Empty
            : string.Join(Separator, cuts.Take(MaxListed).Select(c =>
                $"{c.Employee} {c.PackageStart:MM-dd}..{c.PackageEnd:MM-dd} = {Count(c.LengthDays)}d, cut at "
                + $"{c.CutAt:MM-dd}, {Count(c.MissingDays)} day(s) missing, forced, cause={c.Cause}"));

    /// <summary>The comparison against the stored control run.</summary>
    /// <param name="diff">Baseline comparison of the run</param>
    public static string DescribeDiff(BaselineDiff diff)
    {
        if (!diff.IsAvailable)
        {
            return $"not available ({string.Join(Separator, diff.Notes)})";
        }

        var changed = diff.NightDiffs.Where(d => d.Delta != 0).ToList();
        return $"{diff.BaselineLabel} in {diff.Mode} mode from '{diff.BaselineSource}': "
            + $"{Count(changed.Count)} employee(s) changed their night count, "
            + $"{Count(diff.UnexplainedNightCount)} unexplained. "
            + (changed.Count == 0
                ? "No night count moved."
                : string.Join(Separator, changed.Take(MaxListed).Select(d =>
                    $"{d.Employee} {Count(d.BaselineNights)}->{Count(d.CurrentNights)} [{d.AttributedTo}: "
                    + $"{d.Explanation}]")));
    }

    /// <summary>Planned hours per employee against the stored control run.</summary>
    /// <param name="diffs">Hour differences of the run</param>
    public static string DescribeHoursDiff(IReadOnlyList<BaselineHoursDiff> diffs)
        => diffs.Count == 0
            ? Empty
            : string.Join(Separator, diffs
                .Where(d => Math.Abs(d.Delta) > double.Epsilon)
                .Select(d => $"{d.Employee} {Hours(d.BaselineHours)}->{Hours(d.CurrentHours)} h"));

    private static string Kind(AutofillShiftKind? kind) => kind?.ToString() ?? Empty;

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Hours(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Percent(double value)
        => (value * 100).ToString("0.0", CultureInfo.InvariantCulture) + " %";
}
