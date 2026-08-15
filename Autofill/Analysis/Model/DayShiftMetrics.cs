// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// The day-shift view of a plan: every day-shift assignment and how the day shifts are spread over the
/// roster. Empty for a scenario that cuts no day shift, so every scenario before scenario 5 measures
/// exactly what it measured before.
/// </summary>
/// <param name="Assignments">Every day-shift assignment, ordered by date and order</param>
/// <param name="ShareByEmployee">Day-shift share per employee, in list order</param>
public sealed record DayShiftMetrics(
    IReadOnlyList<DayShiftAssignment> Assignments,
    IReadOnlyList<DayShiftShare> ShareByEmployee)
{
    /// <summary>The measurement of a scenario without day shifts.</summary>
    public static DayShiftMetrics Empty { get; } = new([], []);
}
