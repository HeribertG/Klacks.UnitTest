// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// Who may hold one demanded slot, derived from the fixture ban list — an input-side statement about
/// the slot, independent of who the engine actually planned onto it.
/// </summary>
/// <param name="Date">Calendar day the slot starts on</param>
/// <param name="ShiftType">Kind of the slot per the fixture's shift-kind map</param>
/// <param name="EligibleEmployees">Employees not banned from the slot, in list order</param>
/// <param name="PoolSize">Number of eligible employees</param>
public sealed record ShiftEligibilityPool(
    DateOnly Date,
    AutofillShiftKind ShiftType,
    IReadOnlyList<string> EligibleEmployees,
    int PoolSize);
