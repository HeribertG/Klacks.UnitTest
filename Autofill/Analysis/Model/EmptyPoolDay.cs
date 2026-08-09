// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// A slot no employee may hold: the ban list makes it unfillable, so an unfilled shift here is the
/// input's doing, not the engine's.
/// </summary>
/// <param name="Date">Calendar day the slot starts on</param>
/// <param name="ShiftType">Kind of the slot per the fixture's shift-kind map</param>
public sealed record EmptyPoolDay(
    DateOnly Date,
    AutofillShiftKind ShiftType);
