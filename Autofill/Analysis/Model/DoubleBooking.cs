// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// Two assignments of one employee that cannot both stand: either the same shift was handed to him
/// twice or the two shifts overlap in time. Two DIFFERENT shifts on the same calendar day that do not
/// overlap are not listed — the owner correction of 2026-08-08 removed them from rule 1, and rule 11
/// asks for them when the day would otherwise leave the employee short of hours.
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="Date">Calendar day the earlier of the two shifts starts on</param>
/// <param name="Shifts">The two conflicting shifts with their absolute times, the earlier one first</param>
/// <param name="Reason">Why the two conflict; one of the two reason texts below</param>
public sealed record DoubleBooking(string Employee, DateOnly Date, IReadOnlyList<string> Shifts, string Reason)
{
    /// <summary>Reason text for the same shift handed to the same employee more than once.</summary>
    public const string SameShiftTwiceReason = "the same shift is assigned twice";

    /// <summary>Reason text for two shifts of one employee whose times overlap.</summary>
    public const string OverlappingTimesReason = "the two shifts overlap in time";
}
