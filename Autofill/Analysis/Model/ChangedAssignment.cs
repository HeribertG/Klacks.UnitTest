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
    string? EmployeeTreatment)
{
    /// <summary>
    /// Shift reference of the slot. Date and shift CLASS stopped identifying a slot the moment one
    /// order gained a second slot of the late class — a day shift and a late shift of the same order
    /// share a date and a class — so anything that has to look a slot up, such as a blacklist or a
    /// preference bound to a shift reference, must key on this and not on the pair above.
    /// </summary>
    public Guid ShiftRefId { get; init; }

    /// <summary>
    /// Slot kind of the slot, which separates a day shift from a late one;
    /// <see cref="ShiftType"/> for a shift reference the catalog does not know.
    /// </summary>
    public AutofillShiftKind SlotKind => AutofillShiftCatalog.SlotKindOf(ShiftRefId) ?? ShiftType;

    /// <summary>Order the slot belongs to, resolved from <see cref="ShiftRefId"/>.</summary>
    public int Order => AutofillShiftCatalog.OrderOf(ShiftRefId);
}
