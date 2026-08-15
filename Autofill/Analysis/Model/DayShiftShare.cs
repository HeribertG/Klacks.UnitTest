// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// How much of one employee's workload is day shifts. Counted over in-period assignments only, the
/// same scope coverage, hours and fairness use, because the question is what THIS run planned.
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="DayShiftCount">In-period day shifts the employee holds</param>
/// <param name="TotalCount">All in-period shifts the employee holds</param>
/// <param name="Share">Day shifts divided by all shifts; 0 for an employee without any shift</param>
/// <param name="PrefersDayShifts">True when the employee carries a Preferred entry on a day shift</param>
public sealed record DayShiftShare(
    string Employee,
    int DayShiftCount,
    int TotalCount,
    double Share,
    bool PrefersDayShifts);
