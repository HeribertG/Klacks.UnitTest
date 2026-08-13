// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// A maximal run of consecutive calendar days on which one employee holds at least one shift, built
/// across the month boundary: a run that starts in the carry-in month and reaches into the period is
/// one package with one length and one kind. Only packages holding at least one in-period day are
/// reported; a package that was already closed before the period belongs to the previous month's plan.
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="StartDate">First day of the package; may lie in the carry-in month</param>
/// <param name="EndDate">Last day of the package; always inside the period</param>
/// <param name="LengthDays">Number of calendar days the package spans, carry-in days included</param>
/// <param name="ShiftType">Kind of the first day; with MixedTypes the package is not homogeneous</param>
/// <param name="MixedTypes">True when the package contains more than one shift kind (rule 4)</param>
/// <param name="FirstStartAt">Earliest shift start of the package; carries the clock for rest measurements</param>
/// <param name="LastEndAt">Latest shift end of the package; a closing night shift ends on the following day</param>
public sealed record WorkPackage(
    string Employee,
    DateOnly StartDate,
    DateOnly EndDate,
    int LengthDays,
    AutofillShiftKind ShiftType,
    bool MixedTypes,
    DateTime FirstStartAt,
    DateTime LastEndAt);
