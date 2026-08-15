// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis;

/// <summary>
/// Compares a finished run against a stored baseline measurement and attributes every difference it
/// finds. It answers the question scenario 6 asks of its control run: what did the absence change,
/// and can every change be traced back to it.
/// </summary>
public static class BaselineDiffAnalyzer
{
    /// <summary>
    /// The attribution rule, written into every artifact so a reader can judge the count instead of
    /// trusting it. It is stated once and is never widened to reach zero: a rule generous enough to
    /// explain every change would explain nothing.
    /// </summary>
    public const string NightAttributionRule =
        "A night-count difference against the baseline is attributed in this order. "
        + "(1) Unchanged: the two counts are equal. "
        + "(2) AbsenceRemoved: an ABSENT employee lost night shifts, capped at what the absence can account for — "
        + "in slot-level mode the baseline nights that fall on days now absent, in aggregate-only mode his number "
        + "of absent days, which is an upper bound on the same quantity. "
        + "(3) AbsenceRedistributed: an employee GAINED night shifts while the budget of nights removed under (2) "
        + "is not yet exhausted; the budget is consumed in list-rank order so the attribution is deterministic. "
        + "(4) Unexplained: everything else — a loss by an employee who is not absent, a loss beyond what his "
        + "absence can account for, and every gain beyond the redistribution budget. Those need a package-level "
        + "justification, which a finished plan does not carry, so they are reported and judged by hand. "
        + "The rule is never widened to drive (4) to zero.";

    /// <summary>
    /// Builds the comparison.
    /// </summary>
    /// <param name="load">Result of looking for the baseline; a missing baseline yields the NotAvailable state</param>
    /// <param name="current">Measurement of this run</param>
    /// <param name="definition">Scenario that produced this run; supplies list order and absences</param>
    /// <param name="baselineLabel">Name of the baseline run for the report, for example S5a</param>
    public static BaselineDiff Build(
        BaselineMetricsLoad load,
        AutofillMetrics current,
        AutofillScenarioDefinition definition,
        string baselineLabel)
    {
        if (load.Snapshot is null)
        {
            return BaselineDiff.None with
            {
                BaselineLabel = baselineLabel,
                BaselineSource = load.Source,
                AttributionRule = NightAttributionRule,
                Notes = [load.Problem ?? "The baseline could not be read."],
            };
        }

        var snapshot = load.Snapshot;
        var mode = snapshot.HasAssignments ? BaselineDiffMode.SlotLevel : BaselineDiffMode.AggregateOnly;
        var notes = new List<string>();
        if (mode == BaselineDiffMode.AggregateOnly)
        {
            notes.Add(
                $"The baseline artifact '{load.Source}' carries no assignment list — it was written before the list "
                + "was added to the schema on 2026-08-14. The comparison is therefore AGGREGATE ONLY: per-employee "
                + "counts are compared, which slot moved is not knowable, and the absence cap of rule (2) is the "
                + "employee's number of absent days rather than his baseline nights inside the window.");
        }

        var nightDiffs = BuildNightDiffs(snapshot, current, definition, mode, notes);

        return new BaselineDiff(
            BaselineLabel: baselineLabel,
            BaselineSource: load.Source,
            Mode: mode,
            AttributionRule: NightAttributionRule,
            NightDiffs: nightDiffs,
            HoursDiffs: BuildHoursDiffs(snapshot, current, definition),
            Notes: notes);
    }

    private static IReadOnlyList<BaselineNightDiff> BuildNightDiffs(
        BaselineMetricsSnapshot snapshot,
        AutofillMetrics current,
        AutofillScenarioDefinition definition,
        BaselineDiffMode mode,
        List<string> notes)
    {
        var currentNights = current.Fairness.ShiftTypeCountPerEmployee
            .ToDictionary(c => c.Employee, c => c.Night, StringComparer.Ordinal);

        var raw = definition.EmployeesInListOrder
            .Select(employee => (
                Employee: employee,
                Rank: definition.ListRankOf(employee),
                Baseline: snapshot.NightsOf(employee),
                Current: currentNights.TryGetValue(employee, out var nights) ? nights : 0))
            .ToList();

        var budget = 0;
        var attributed = new Dictionary<string, (BaselineDiffAttribution Cause, string Why)>(StringComparer.Ordinal);

        foreach (var entry in raw.Where(e => e.Current < e.Baseline))
        {
            var lost = entry.Baseline - entry.Current;
            var cap = AbsenceCapOf(entry.Employee, snapshot, definition, mode);
            if (cap <= 0)
            {
                attributed[entry.Employee] = (
                    BaselineDiffAttribution.Unexplained,
                    $"lost {Count(lost)} night(s) although no absence of his own can account for any of them");
                continue;
            }

            if (lost > cap)
            {
                attributed[entry.Employee] = (
                    BaselineDiffAttribution.Unexplained,
                    $"lost {Count(lost)} night(s), of which the absence accounts for at most {Count(cap)}");
                budget += cap;
                continue;
            }

            attributed[entry.Employee] = (
                BaselineDiffAttribution.AbsenceRemoved,
                $"lost {Count(lost)} night(s); the absence blocks {Count(cap)} of his days, which accounts for them");
            budget += lost;
        }

        var remaining = budget;
        foreach (var entry in raw.Where(e => e.Current > e.Baseline).OrderBy(e => e.Rank))
        {
            var gained = entry.Current - entry.Baseline;
            if (gained <= remaining)
            {
                attributed[entry.Employee] = (
                    BaselineDiffAttribution.AbsenceRedistributed,
                    $"gained {Count(gained)} night(s) out of the {Count(budget)} the absence freed");
                remaining -= gained;
                continue;
            }

            attributed[entry.Employee] = (
                BaselineDiffAttribution.Unexplained,
                $"gained {Count(gained)} night(s) while only {Count(remaining)} of the {Count(budget)} freed by the "
                + "absence were still unassigned");
            remaining = 0;
        }

        if (budget == 0 && raw.Any(e => e.Current != e.Baseline))
        {
            notes.Add(
                "The absence freed no night shift at all, so no gain anywhere can be attributed to it. Every "
                + "difference below therefore needs a package-level justification.");
        }

        return raw
            .Select(entry =>
            {
                var (cause, why) = attributed.TryGetValue(entry.Employee, out var found)
                    ? found
                    : (BaselineDiffAttribution.Unchanged, "holds the same number of night shifts as in the baseline");
                return new BaselineNightDiff(entry.Employee, entry.Rank, entry.Baseline, entry.Current, cause, why);
            })
            .OrderBy(d => d.ListRank)
            .ToList();
    }

    /// <summary>
    /// How many of an employee's lost night shifts his own absence can account for. In slot-level mode
    /// that is exactly the baseline nights standing on days he is now absent; in aggregate-only mode
    /// the baseline slots are unknown and the number of absent days is used, which is an upper bound
    /// on the same quantity and therefore never explains more than the truth would.
    /// </summary>
    /// <param name="employee">Employee identifier</param>
    /// <param name="snapshot">Baseline measurement</param>
    /// <param name="definition">Scenario carrying the absences</param>
    /// <param name="mode">How much of the baseline the comparison can use</param>
    private static int AbsenceCapOf(
        string employee,
        BaselineMetricsSnapshot snapshot,
        AutofillScenarioDefinition definition,
        BaselineDiffMode mode)
    {
        var absentDays = definition.Absences.DaysOf(employee);
        if (absentDays.Count == 0)
        {
            return 0;
        }

        if (mode != BaselineDiffMode.SlotLevel)
        {
            return absentDays.Count;
        }

        var absent = absentDays.ToHashSet();
        return snapshot.Assignments.Count(row =>
            string.Equals(row.Employee, employee, StringComparison.Ordinal)
            && AutofillShiftCatalog.ShiftClassOf(row.SlotKind) == AutofillShiftKind.Night
            && absent.Contains(row.Date));
    }

    private static IReadOnlyList<BaselineHoursDiff> BuildHoursDiffs(
        BaselineMetricsSnapshot snapshot,
        AutofillMetrics current,
        AutofillScenarioDefinition definition)
    {
        var currentHours = current.Hours.PerEmployee
            .ToDictionary(h => h.Employee, h => h.PlannedHours, StringComparer.Ordinal);

        return definition.EmployeesInListOrder
            .Select(employee => new BaselineHoursDiff(
                employee,
                definition.ListRankOf(employee),
                snapshot.HoursOf(employee),
                currentHours.TryGetValue(employee, out var hours) ? hours : 0))
            .OrderBy(d => d.ListRank)
            .ToList();
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
