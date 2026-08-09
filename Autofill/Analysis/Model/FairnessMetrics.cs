// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>Rule 7: how evenly the shift kinds are spread over the employees.</summary>
/// <param name="ShiftTypeCountPerEmployee">Per-employee counts in list order</param>
/// <param name="SpreadPerType">Highest minus lowest count per kind, over all employees</param>
/// <param name="GiniPerType">Gini coefficient of the counts per kind; 0 = perfectly even</param>
/// <param name="NightSharePerEligibleDay">
/// Night load normalised to each employee's ban-list-eligible night days, in list order. Empty
/// without an eligibility input
/// </param>
/// <param name="CohortSpreadNight">
/// Highest minus lowest normalised night share inside the night cohort — the employees with at least
/// one eligible night day. 0 without an eligibility input or with a cohort of at most one
/// </param>
public sealed record FairnessMetrics(
    IReadOnlyList<EmployeeShiftTypeCounts> ShiftTypeCountPerEmployee,
    ShiftTypeCountTriple SpreadPerType,
    ShiftTypeRatioTriple GiniPerType,
    IReadOnlyList<NightEligibilityShare> NightSharePerEligibleDay,
    double CohortSpreadNight);
