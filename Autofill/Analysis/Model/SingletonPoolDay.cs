// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// A slot whose eligible pool holds exactly one employee: the assignment is factually determined by
/// the ban list before the engine even runs, so nothing about this slot measures the algorithm.
/// </summary>
/// <param name="Date">Calendar day the slot starts on</param>
/// <param name="ShiftType">Kind of the slot per the fixture's shift-kind map</param>
/// <param name="Employee">The one employee who may hold it</param>
public sealed record SingletonPoolDay(
    DateOnly Date,
    AutofillShiftKind ShiftType,
    string Employee);
