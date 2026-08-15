// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Analysis.Model;

namespace Klacks.UnitTest.Autofill.Analysis;

/// <summary>
/// The part of a stored measurement a later run can compare itself against: who worked how many
/// shifts of which class, how many hours each of them was planned, and — when the artifact is new
/// enough to carry it — the assignment list itself.
/// <para>
/// It is deliberately a NARROW view of the artifact and not the whole metric record. A baseline is
/// read from a file another session wrote, possibly against an older schema; deserialising the entire
/// graph would make an unrelated field able to break the comparison, and a comparison that breaks is
/// indistinguishable from one that finds nothing.
/// </para>
/// </summary>
/// <param name="TestName">Test name the artifact carries, for example Scenario5a</param>
/// <param name="RunLabel">Run label the artifact carries, for example run1</param>
/// <param name="ShiftTypeCountPerEmployee">Per-employee early/late/night counts of the baseline run</param>
/// <param name="HoursPerEmployee">Per-employee planned hours of the baseline run</param>
/// <param name="Assignments">
/// Assignment list of the baseline run; empty for an artifact written before the list was added to
/// the schema on 2026-08-14, which is what makes the difference between a slot-level and an
/// aggregate-only comparison
/// </param>
public sealed record BaselineMetricsSnapshot(
    string TestName,
    string RunLabel,
    IReadOnlyList<EmployeeShiftTypeCounts> ShiftTypeCountPerEmployee,
    IReadOnlyList<EmployeeHours> HoursPerEmployee,
    IReadOnlyList<PlanAssignmentRow> Assignments)
{
    /// <summary>Night shifts the baseline gave one employee; 0 when it does not know him.</summary>
    /// <param name="employee">Employee identifier</param>
    public int NightsOf(string employee)
        => ShiftTypeCountPerEmployee
            .Where(c => string.Equals(c.Employee, employee, StringComparison.Ordinal))
            .Sum(c => c.Night);

    /// <summary>Planned hours the baseline gave one employee; 0 when it does not know him.</summary>
    /// <param name="employee">Employee identifier</param>
    public double HoursOf(string employee)
        => HoursPerEmployee
            .Where(h => string.Equals(h.Employee, employee, StringComparison.Ordinal))
            .Sum(h => h.PlannedHours);

    /// <summary>True when the artifact carries its assignment list and slots can be compared one by one.</summary>
    public bool HasAssignments => Assignments.Count > 0;
}
