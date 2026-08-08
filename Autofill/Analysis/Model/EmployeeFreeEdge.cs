// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// Free days at the two ends of the period. They are not free blocks in the sense of rule 3 — nothing
/// bounds them on the outside — so they are reported separately instead of distorting the histogram.
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="LeadingFreeDays">Free days between the period start and the first package</param>
/// <param name="TrailingFreeDays">Free days between the last package and the period end</param>
public sealed record EmployeeFreeEdge(string Employee, int LeadingFreeDays, int TrailingFreeDays);
