// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// One demanded slot that the baseline plan and the treatment plan staff differently. Null means the
/// slot is unfilled in that plan; a slot oversupplied with several employees is reported as their
/// names joined in a stable order, so even a pathological plan diffs deterministically.
/// </summary>
/// <param name="Date">Calendar day the slot starts on</param>
/// <param name="ShiftType">Kind of the slot</param>
/// <param name="EmployeeBaseline">Who holds the slot in the baseline plan, or null when nobody does</param>
/// <param name="EmployeeTreatment">Who holds the slot in the treatment plan, or null when nobody does</param>
public sealed record ChangedAssignment(
    DateOnly Date,
    AutofillShiftKind ShiftType,
    string? EmployeeBaseline,
    string? EmployeeTreatment);
