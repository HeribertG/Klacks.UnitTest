// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// One shift of one employee, flattened out of the engine's token model so the analyzer and the
/// matrix renderer can treat a planned shift and a carried-in shift the same way.
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="Date">Calendar day the shift starts on</param>
/// <param name="Kind">Early, late or night</param>
/// <param name="StartAt">Absolute start</param>
/// <param name="EndAt">Absolute end; for a night shift this lies on the following day</param>
/// <param name="Hours">Paid hours</param>
/// <param name="IsCarryIn">True when the shift comes from the month before the period and is fixed input</param>
public sealed record PlannedShift(
    string Employee,
    DateOnly Date,
    AutofillShiftKind Kind,
    DateTime StartAt,
    DateTime EndAt,
    double Hours,
    bool IsCarryIn);
