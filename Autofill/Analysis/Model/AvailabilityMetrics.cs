// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// The capacity arithmetic of a scenario, computed from the FIXTURE alone and therefore knowable
/// before the engine starts. It is what separates "the algorithm produced a bad rhythm" from "no
/// rhythm was arithmetically available", and scenario 6 requires it to be logged before every run.
/// </summary>
/// <param name="SlotsTotal">
/// Employee days the roster offers: employees times period days, minus every day an absence closes.
/// An absent day is not capacity, so it is subtracted rather than counted and then forbidden
/// </param>
/// <param name="RequiredAssignments">Assignments the period demands over all orders and days</param>
/// <param name="WorkRatioRequired">Required assignments divided by <paramref name="SlotsTotal"/></param>
/// <param name="MaxRatioUnder52">Work share a clean five-work/two-free rhythm reaches: 5 of 7</param>
/// <param name="ForcedExtraShifts">
/// Assignments beyond what a clean 5/2 rhythm can carry, rounded up: the number of extensions or
/// shortened free blocks the arithmetic forces on the plan. 0 when 5/2 is still reachable
/// </param>
/// <param name="TightestDay">The day with the worst ratio; null for a scenario with no demanded day</param>
/// <param name="AbsenceWindowSlots">Employee days inside the absence windows, absent days excluded</param>
/// <param name="AbsenceWindowRequired">Assignments demanded inside the absence windows</param>
public sealed record AvailabilityMetrics(
    int SlotsTotal,
    int RequiredAssignments,
    double WorkRatioRequired,
    double MaxRatioUnder52,
    int ForcedExtraShifts,
    TightestDay? TightestDay,
    int AbsenceWindowSlots,
    int AbsenceWindowRequired)
{
    /// <summary>The measurement of a scenario nothing was computed for.</summary>
    public static AvailabilityMetrics Empty { get; } = new(0, 0, 0, 0, 0, null, 0, 0);

    /// <summary>Work share the absence windows demand of the employees still available in them.</summary>
    public double AbsenceWindowWorkRatio
        => AbsenceWindowSlots == 0 ? 0 : AbsenceWindowRequired / (double)AbsenceWindowSlots;
}
