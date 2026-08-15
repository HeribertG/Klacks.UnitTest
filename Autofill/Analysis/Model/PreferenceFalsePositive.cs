// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// One assignment that grants a soft preference at the price of a hard rule — the question "was
/// anything harder sacrificed to the preference?".
/// <para>
/// A preference is the weakest input the engine takes: it tilts the bidding and nothing more. It may
/// therefore never be the reason a plan breaks a rule that is not negotiable. This record names an
/// assignment that satisfies a Preferred entry AND appears in one of the hard findings of the same
/// plan, so the two can be read together instead of each looking innocent on its own.
/// </para>
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="Date">Calendar day of the assignment</param>
/// <param name="Order">Order the shift belongs to</param>
/// <param name="SlotKind">Slot kind of the preferred shift</param>
/// <param name="ShiftRefId">Shift reference id the Preferred entry names</param>
/// <param name="HardRuleBroken">Name of the hard finding this assignment takes part in</param>
public sealed record PreferenceFalsePositive(
    string Employee,
    DateOnly Date,
    int Order,
    AutofillShiftKind SlotKind,
    Guid ShiftRefId,
    string HardRuleBroken);
