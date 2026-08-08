// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>Rule 7: how evenly the shift kinds are spread over the employees.</summary>
/// <param name="ShiftTypeCountPerEmployee">Per-employee counts in list order</param>
/// <param name="SpreadPerType">Highest minus lowest count per kind, over all employees</param>
/// <param name="GiniPerType">Gini coefficient of the counts per kind; 0 = perfectly even</param>
public sealed record FairnessMetrics(
    IReadOnlyList<EmployeeShiftTypeCounts> ShiftTypeCountPerEmployee,
    ShiftTypeCountTriple SpreadPerType,
    ShiftTypeRatioTriple GiniPerType);
