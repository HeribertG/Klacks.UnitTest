// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// A gap between two work packages that granted fewer free days than the contract asks for. Absence
/// days are not counted as free days here, exactly as in the free-block histogram, so a gap made
/// entirely of holiday is not a shortened free block — it is no free block at all.
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="FromInclusive">First day of the gap</param>
/// <param name="UntilInclusive">Last day of the gap</param>
/// <param name="FreeDays">Days of the gap that are genuinely free</param>
/// <param name="MissingDays">Days the gap falls short of the contractual rest</param>
/// <param name="TouchesAbsenceWindow">
/// True when the gap lies inside or directly next to an absence window of any employee. False for
/// every scenario without absences
/// </param>
public sealed record ShortenedFreeBlock(
    string Employee,
    DateOnly FromInclusive,
    DateOnly UntilInclusive,
    int FreeDays,
    int MissingDays,
    bool TouchesAbsenceWindow);
