// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// One employee's night load normalised to the days the ban list lets him work nights at all. An
/// equal-share comparison over the whole roster is meaningless when two employees can never take a
/// night and a third only two thirds of the month; dividing by the eligible days makes the shares
/// comparable inside the night cohort. An employee with zero eligible days reports a share of zero —
/// he has no denominator and stands outside the cohort.
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="EligibleDays">Days of the period with a night slot the employee is not banned from</param>
/// <param name="NightShifts">Night shifts the plan gives the employee inside the period</param>
/// <param name="SharePerEligibleDay">NightShifts divided by EligibleDays; 0 when EligibleDays is 0</param>
public sealed record NightEligibilityShare(
    string Employee,
    int EligibleDays,
    int NightShifts,
    double SharePerEligibleDay);
