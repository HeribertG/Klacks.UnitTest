// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// The three-dimensional continuation check of a carried-in package: order, shift kind and remaining
/// length together. Kept apart from <see cref="CarryInRespect"/>, whose two-dimensional verdict the
/// carry-in scenarios pin, so adding the order dimension cannot move an existing measurement.
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="ExpectedOrder">Order the first in-period shift must belong to; null when the specification fixes none</param>
/// <param name="ActualOrder">Order it belongs to; null when the employee received no in-period shift</param>
/// <param name="ExpectedShiftType">Shift kind the first in-period shift must have</param>
/// <param name="ActualShiftType">Kind of the first in-period shift; null when the employee received none</param>
/// <param name="ExpectedRemainingDays">In-period days the open package still has to run on that order and kind</param>
/// <param name="ActualRemainingDays">In-period days it actually ran on that order and kind</param>
/// <param name="Ok">True when order, kind and remaining days all match</param>
public sealed record CarryInOrderRespect(
    string Employee,
    int? ExpectedOrder,
    int? ActualOrder,
    AutofillShiftKind ExpectedShiftType,
    AutofillShiftKind? ActualShiftType,
    int ExpectedRemainingDays,
    int ActualRemainingDays,
    bool Ok)
{
    /// <summary>
    /// Slot kind of the first in-period shift; differs from <see cref="ActualShiftType"/> only where a
    /// day shift stands behind a late class. Reported for readability, never part of the verdict.
    /// </summary>
    public AutofillShiftKind? ActualFirstSlotKind { get; init; }
}
