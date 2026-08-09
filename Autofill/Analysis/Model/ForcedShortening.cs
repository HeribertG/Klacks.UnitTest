// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// A package shorter than the five-day ideal that the ban list provably forced: within the window it
/// falls into, the eligible pool of its shift kind cannot cover the demand with full five-day
/// packages alone, so at least this many shifts had to land in a shortened package. The proof is
/// spelled out in <paramref name="ProvableCause"/> so the report never claims "forced" without
/// showing the arithmetic.
/// </summary>
/// <param name="Employee">Employee holding the shortened package</param>
/// <param name="StartDate">First day of the shortened package</param>
/// <param name="LengthDays">Length of the shortened package in days</param>
/// <param name="ProvableCause">The capacity argument that makes the shortening unavoidable</param>
public sealed record ForcedShortening(
    string Employee,
    DateOnly StartDate,
    int LengthDays,
    string ProvableCause);
