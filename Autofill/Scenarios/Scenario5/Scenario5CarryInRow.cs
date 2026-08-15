// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario5;

/// <summary>
/// One row of the specification's table of packages that are still running when the period starts.
/// The served days follow from the start date and the end of the carry-in month, so the table states
/// the start and the guard recomputes everything else.
/// </summary>
/// <param name="AgentId">Employee the package belongs to</param>
/// <param name="OrderLabel">Order label of the specification table, 1 to 3</param>
/// <param name="Kind">Slot kind of every day of the package; the day shift is a kind of its own here</param>
/// <param name="PackageStart">First day of the package, in the carry-in month</param>
public sealed record Scenario5CarryInRow(
    string AgentId,
    int OrderLabel,
    AutofillShiftKind Kind,
    DateOnly PackageStart);
