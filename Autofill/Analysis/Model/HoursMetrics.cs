// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>Rule 6: guaranteed hours served top-down in list order.</summary>
/// <param name="PerEmployee">One entry per employee, in list order</param>
/// <param name="FulfillmentByRank">Fulfilment shares in list order; expected to never rise</param>
/// <param name="MonotonicityViolations">Ranks where the fulfilment rises against the list order</param>
public sealed record HoursMetrics(
    IReadOnlyList<EmployeeHours> PerEmployee,
    IReadOnlyList<double> FulfillmentByRank,
    IReadOnlyList<MonotonicityViolation> MonotonicityViolations);
