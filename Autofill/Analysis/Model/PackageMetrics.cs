// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>Rules 3 and 4: package integrity and shift-kind homogeneity inside a package.</summary>
/// <param name="Items">Every package of every employee, ordered by list rank and start date</param>
/// <param name="LengthHistogram">Package count per length bucket, "1".."6" and "7+"</param>
/// <param name="FreeBlockHistogram">Count of free blocks per length, period edges excluded</param>
/// <param name="IdealShare">Share of packages of length 5 followed by exactly 2 free days</param>
/// <param name="MixedTypeCount">Packages that change shift kind inside the package</param>
/// <param name="FreeEdges">Free days at the period boundaries, reported but not counted as free blocks</param>
public sealed record PackageMetrics(
    IReadOnlyList<WorkPackage> Items,
    IReadOnlyDictionary<string, int> LengthHistogram,
    IReadOnlyDictionary<int, int> FreeBlockHistogram,
    double IdealShare,
    int MixedTypeCount,
    IReadOnlyList<EmployeeFreeEdge> FreeEdges);
