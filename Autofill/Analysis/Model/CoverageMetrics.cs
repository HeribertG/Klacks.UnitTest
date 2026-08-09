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
    int OversuppliedSlots)
{
    /// <summary>The same coverage per order, so no order can hide behind the total. Empty without an order dimension.</summary>
    public IReadOnlyList<OrderCoverage> PerOrder { get; init; } = [];

    /// <summary>Assignments per calendar day of the period, one entry per day, always in date order.</summary>
    public IReadOnlyList<DayAssignmentCount> AssignmentsPerDay { get; init; } = [];

    /// <summary>Days on which one employee holds shifts of more than one order. Empty without an order dimension.</summary>
    public IReadOnlyList<CrossOrderDoubleBooking> CrossOrderDoubleBookings { get; init; } = [];
}
