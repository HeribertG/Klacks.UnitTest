// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// Rule 10 (same order inside a package) as a set of numbers. Every field stays empty in a scenario
/// without an order dimension, because there is nothing to be loyal to there.
/// </summary>
/// <param name="SwitchesWithinPackage">Packages that touch more than one order</param>
/// <param name="SwitchesPerEmployee">Order changes per employee over the whole period</param>
/// <param name="EmployeeDistribution">Which employees staff which order, with the mean list rank</param>
public sealed record OrderMetrics(
    IReadOnlyList<OrderSwitchInPackage> SwitchesWithinPackage,
    IReadOnlyList<OrderSwitchesPerEmployee> SwitchesPerEmployee,
    IReadOnlyList<OrderEmployeeDistribution> EmployeeDistribution)
{
    /// <summary>The measurement of a scenario that plans a single, unnamed order.</summary>
    public static OrderMetrics Empty { get; } = new([], [], []);
}
