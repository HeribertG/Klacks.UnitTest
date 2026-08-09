// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario4;

/// <summary>One row of the specification table of packages that are still running on 1 March.</summary>
/// <param name="AgentId">Employee identifier</param>
/// <param name="OrderLabel">Order of the specification table, 1 to 3</param>
/// <param name="Kind">Shift kind of every day of the package</param>
/// <param name="PackageStart">First February day of the package; it always runs to 28 February</param>
public sealed record Scenario4CarryInRow(
    string AgentId,
    int OrderLabel,
    AutofillShiftKind Kind,
    DateOnly PackageStart);
