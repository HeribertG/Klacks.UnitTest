// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario4;

/// <summary>One row of the specification table of packages that closed before the period starts.</summary>
/// <param name="AgentId">Employee identifier</param>
/// <param name="OrderLabel">Order of the specification table, 1 to 3</param>
/// <param name="Kind">Shift kind of the closed package</param>
/// <param name="PackageEndInclusive">Last February day of the package</param>
/// <param name="NextKind">Shift kind the forward rotation owes the employee next</param>
public sealed record Scenario4ClosedCarryInRow(
    string AgentId,
    int OrderLabel,
    AutofillShiftKind Kind,
    DateOnly PackageEndInclusive,
    AutofillShiftKind NextKind);
