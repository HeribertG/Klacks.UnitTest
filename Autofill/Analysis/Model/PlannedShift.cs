// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// One shift of one employee, flattened out of the engine's token model so the analyzer and the
/// matrix renderer can treat a planned shift and a carried-in shift the same way.
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="Date">Calendar day the shift starts on</param>
/// <param name="Kind">
/// Engine shift CLASS: early, late or night. A day shift carries <c>Late</c> here, because that is
/// what the engine inferred from its span and what every rotation, purity and fairness measure must
/// see. Use <see cref="PlannedShift.SlotKind"/> to tell a day shift from a late one
/// </param>
/// <param name="StartAt">Absolute start</param>
/// <param name="EndAt">Absolute end; for a night shift this lies on the following day</param>
/// <param name="Hours">Paid hours</param>
/// <param name="IsCarryIn">True when the shift comes from the month before the period and is fixed input</param>
/// <param name="ShiftRefId">Id of the demanded slot this shift staffs — the only carrier of the order</param>
/// <param name="Order">
/// Order the shift belongs to, resolved from <paramref name="ShiftRefId"/>;
/// <see cref="AutofillShiftCatalog.SingleOrderIndex"/> in a scenario without an order dimension
/// </param>
public sealed record PlannedShift(
    string Employee,
    DateOnly Date,
    AutofillShiftKind Kind,
    DateTime StartAt,
    DateTime EndAt,
    double Hours,
    bool IsCarryIn,
    Guid ShiftRefId,
    int Order)
{
    /// <summary>
    /// Slot kind the shift staffs, resolved from <see cref="ShiftRefId"/>: the same value as
    /// <see cref="Kind"/> for the three classic shifts, and <c>Day</c> for the day shift, which the
    /// engine cannot distinguish from a late one. Falls back to <see cref="Kind"/> for a shift
    /// reference the catalog does not know, so a scenario without day shifts measures exactly what it
    /// measured before the slot kind existed.
    /// </summary>
    public AutofillShiftKind SlotKind => AutofillShiftCatalog.SlotKindOf(ShiftRefId) ?? Kind;

    /// <summary>True when the shift staffs a day shift slot.</summary>
    public bool IsDayShift => SlotKind == AutofillShiftKind.Day;
}
