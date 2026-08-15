// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>Rule 5 (11-rule order, SPEC decision 9): guaranteed hours served top-down in list order.</summary>
/// <param name="PerEmployee">One entry per employee, in list order</param>
/// <param name="FulfillmentByRank">Fulfilment shares in list order; expected to never rise</param>
/// <param name="MonotonicityViolations">Ranks where the fulfilment rises against the list order</param>
public sealed record HoursMetrics(
    IReadOnlyList<EmployeeHours> PerEmployee,
    IReadOnlyList<double> FulfillmentByRank,
    IReadOnlyList<MonotonicityViolation> MonotonicityViolations)
{
    /// <summary>Employees the plan left without a single in-period shift, in list order.</summary>
    public IReadOnlyList<string> ZeroHourEmployees { get; init; } = [];

    /// <summary>
    /// The hour target seen the way the engine sees it once absences are booked: the unchanged target
    /// against the coverage sum that includes the absence credit. Empty for a scenario without
    /// absences — there the credit is zero and this list would only restate
    /// <see cref="PerEmployee"/>.
    /// </summary>
    public IReadOnlyList<CreditTargetEntry> CreditTarget { get; init; } = [];
}
