// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>One employee's planned hours in the baseline run next to the same figure in this run.</summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="ListRank">Position in the displayed list, 1 = top</param>
/// <param name="BaselineHours">Hours the baseline run planned for him</param>
/// <param name="CurrentHours">Hours this run plans for him</param>
public sealed record BaselineHoursDiff(
    string Employee,
    int ListRank,
    double BaselineHours,
    double CurrentHours)
{
    /// <summary>Current minus baseline; negative when the employee lost hours.</summary>
    public double Delta => CurrentHours - BaselineHours;
}
