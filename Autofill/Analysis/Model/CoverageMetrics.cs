// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>Rule 1: is every demanded shift staffed exactly once.</summary>
/// <param name="TotalRequiredShifts">Sum of the required assignments over all demanded slots</param>
/// <param name="FilledShifts">Slots that received at least one employee, capped at their demand</param>
/// <param name="UnfilledShifts">Slots nobody received</param>
/// <param name="DoubleBookings">
/// Conflicting pairs of assignments: the same shift given to one employee twice, or two shifts of one
/// employee whose times overlap. Two different, non-overlapping shifts on one day are not a conflict
/// </param>
/// <param name="OversuppliedSlots">Slots that received more employees than they demand</param>
public sealed record CoverageMetrics(
    int TotalRequiredShifts,
    int FilledShifts,
    IReadOnlyList<UnfilledShift> UnfilledShifts,
    IReadOnlyList<DoubleBooking> DoubleBookings,
    int OversuppliedSlots);
