// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>Whether one carried-in package was continued the way the specification requires.</summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="ExpectedShiftType">Shift kind the first in-period shift must have</param>
/// <param name="ActualFirstShiftType">Kind of the first in-period shift; null when the employee got none</param>
/// <param name="ExpectedRemainingDays">In-period days the open package still has to run</param>
/// <param name="ActualRemainingDays">In-period days it actually ran, counted from the first day of the period</param>
/// <param name="Ok">True when kind and remaining days both match</param>
/// <param name="ActualPackageDays">
/// Length of the whole package that covers the month boundary, carry-in days included; 0 when the
/// employee holds no shift on the first day of the period and the package was therefore not continued.
/// Diagnosis only — it never enters <paramref name="Ok"/>.
/// </param>
public sealed record CarryInRespect(
    string Employee,
    AutofillShiftKind ExpectedShiftType,
    AutofillShiftKind? ActualFirstShiftType,
    int ExpectedRemainingDays,
    int ActualRemainingDays,
    bool Ok,
    int ActualPackageDays)
{
    /// <summary>
    /// Slot kind of the first in-period shift, which differs from
    /// <see cref="ActualFirstShiftType"/> only where a day shift stands behind a late class. Reported
    /// so a carry-in verdict about a day-shift package stays readable; the verdict itself is
    /// <see cref="Ok"/>.
    /// </summary>
    public AutofillShiftKind? ActualFirstSlotKind { get; init; }
}
