// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// One work package whose days do not all belong to the same order — the measurement behind rule 10
/// ("same order inside a package"). Listed, never asserted: no engine mechanism keeps an employee on
/// one order, so the count is a number for the owner to decide on, not a verdict.
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="PackageStart">First day of the package, carry-in days included</param>
/// <param name="Orders">Orders the package touches, in the sequence the days run</param>
public sealed record OrderSwitchInPackage(
    string Employee,
    DateOnly PackageStart,
    IReadOnlyList<int> Orders);
