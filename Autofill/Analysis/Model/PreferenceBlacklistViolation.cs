// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// One assignment the plan holds although the employee is blacklisted from that shift. Found by an
/// INDEPENDENT scan of the finished plan against the fixture preference list, never from the engine's
/// own violation list: a blacklisted assignment is not a <c>ViolationKind</c> at all
/// (<c>Constraints/ViolationKind.cs</c> has no member for it), so the engine never reports one and a
/// measurement that trusted it would always read zero.
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="Date">Calendar day of the assignment</param>
/// <param name="Order">Order the shift belongs to</param>
/// <param name="SlotKind">Slot kind of the shift the employee is blacklisted from</param>
/// <param name="ShiftRefId">Shift reference id the blacklist entry names</param>
/// <param name="IsCarryIn">
/// True when the offending shift is fixed previous-month input rather than something this run planned.
/// A carry-in day that breaks a blacklist is a FIXTURE mistake, not an engine defect
/// </param>
public sealed record PreferenceBlacklistViolation(
    string Employee,
    DateOnly Date,
    int Order,
    AutofillShiftKind SlotKind,
    Guid ShiftRefId,
    bool IsCarryIn);
