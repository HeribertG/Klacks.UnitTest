// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// How often one employee changes order over the whole period, package borders included — the
/// measurement behind order loyalty across packages.
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="SwitchCount">Changes between two consecutive shifts of different orders</param>
/// <param name="Sequence">Order of every shift of the employee, in chronological sequence</param>
public sealed record OrderSwitchesPerEmployee(
    string Employee,
    int SwitchCount,
    IReadOnlyList<int> Sequence);
