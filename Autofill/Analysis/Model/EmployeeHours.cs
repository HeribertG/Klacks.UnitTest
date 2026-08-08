// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>How far one employee's guaranteed hours were served.</summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="ListRank">Position in the displayed list, 1 = top</param>
/// <param name="GuaranteedHours">Contractual target for the period</param>
/// <param name="PlannedHours">Hours the plan assigns inside the period</param>
/// <param name="FulfillmentPct">PlannedHours divided by GuaranteedHours; 0 when there is no target</param>
public sealed record EmployeeHours(
    string Employee,
    int ListRank,
    double GuaranteedHours,
    double PlannedHours,
    double FulfillmentPct);
