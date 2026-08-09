// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// One employee holding shifts of more than one order on the same calendar day. Reported separately
/// from <see cref="DoubleBooking"/>, which asks whether two assignments conflict in time: this measure
/// asks whether the day crosses an order boundary at all, which is the question scenario 4 poses and
/// which no time comparison answers.
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="Date">Calendar day</param>
/// <param name="Orders">Orders the employee holds a shift of on that day, ascending</param>
/// <param name="ShiftTypes">Kinds of those shifts, in the same sequence as the assignments</param>
public sealed record CrossOrderDoubleBooking(
    string Employee,
    DateOnly Date,
    IReadOnlyList<int> Orders,
    IReadOnlyList<AutofillShiftKind> ShiftTypes);
