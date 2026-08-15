// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario5;

/// <summary>
/// One row of the specification's table of packages that closed before the period starts, together
/// with the shift kind the forward rotation owes the employee next.
/// </summary>
/// <param name="AgentId">Employee the package belonged to</param>
/// <param name="OrderLabel">Order label of the specification table, 1 to 3</param>
/// <param name="Kind">Slot kind of every day of the closed package</param>
/// <param name="PackageEndInclusive">Last day of the package</param>
/// <param name="NextKind">Shift class the rotation owes the employee next; no order is prescribed</param>
public sealed record Scenario5ClosedCarryInRow(
    string AgentId,
    int OrderLabel,
    AutofillShiftKind Kind,
    DateOnly PackageEndInclusive,
    AutofillShiftKind NextKind);
