// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// Free days at the two ends of the period. They are not free blocks in the sense of rule 6, package integrity — nothing
/// bounds them on the outside — so they are reported separately instead of distorting the histogram.
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="LeadingFreeDays">In-period free days before the first package; 0 when that package started in the carry-in month</param>
/// <param name="TrailingFreeDays">Free days between the last package and the period end</param>
public sealed record EmployeeFreeEdge(string Employee, int LeadingFreeDays, int TrailingFreeDays);
