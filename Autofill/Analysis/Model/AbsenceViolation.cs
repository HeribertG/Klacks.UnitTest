// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// An assignment whose own calendar day is a booked absence day of the employee holding it. This is
/// the case the engine itself forbids at every gate that creates a token, so the list must be empty;
/// an entry means a hard veto was bypassed rather than outvoted.
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="Date">Calendar day the assignment starts on, which is also an absence day</param>
/// <param name="SlotKind">Slot kind of the assignment; the day shift is separated from the late shift here</param>
/// <param name="Order">Order the slot belongs to</param>
/// <param name="ShiftRefId">Shift reference of the slot, so the entry is re-checkable</param>
/// <param name="IsCarryIn">True when the assignment is fixed previous-month input rather than planned</param>
public sealed record AbsenceViolation(
    string Employee,
    DateOnly Date,
    AutofillShiftKind SlotKind,
    int Order,
    Guid ShiftRefId,
    bool IsCarryIn);
