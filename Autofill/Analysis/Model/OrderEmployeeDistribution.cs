// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// Who works on one order, and at which list ranks. The mean list rank is the imbalance measure: three
/// identically cut orders must not end up with one of them staffed by the top ranks and another by the
/// bottom ranks.
/// </summary>
/// <param name="Order">Order index</param>
/// <param name="Employees">Employees holding at least one shift of that order, in list order</param>
/// <param name="MeanListRank">Mean list rank weighted by shift count; 0 when the order has no shift</param>
public sealed record OrderEmployeeDistribution(
    int Order,
    IReadOnlyList<string> Employees,
    double MeanListRank);
