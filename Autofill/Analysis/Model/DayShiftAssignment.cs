// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// One assignment that staffs a day shift. The day shift is invisible to the engine's shift class —
/// its span classifies as late — so it is identified here through the shift reference id, which is
/// the only field that separates it from an ordinary late shift.
/// </summary>
/// <param name="Date">Calendar day of the assignment</param>
/// <param name="Order">Order the day shift belongs to</param>
/// <param name="Employee">Employee who holds it</param>
/// <param name="ShiftRefId">Shift reference id of the day shift slot; makes the entry re-checkable</param>
/// <param name="ViaPreference">
/// True when the employee carries a Preferred entry for exactly this shift. False does NOT mean the
/// assignment is wrong: the day shifts outnumber what the preferring employees can work, so most of
/// them necessarily go to employees who never asked for one
/// </param>
/// <param name="IsCarryIn">True when the shift comes from the fixed previous month rather than this run</param>
public sealed record DayShiftAssignment(
    DateOnly Date,
    int Order,
    string Employee,
    Guid ShiftRefId,
    bool ViaPreference,
    bool IsCarryIn);
