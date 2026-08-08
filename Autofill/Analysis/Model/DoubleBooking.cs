// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>One employee holding more than one shift on the same calendar day.</summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="Date">Calendar day</param>
/// <param name="Shifts">Names of the shifts assigned on that day, in start-time order</param>
public sealed record DoubleBooking(string Employee, DateOnly Date, IReadOnlyList<string> Shifts);
