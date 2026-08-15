// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// One command window of the fixture together with everything the plan put inside it. This is the
/// readable half of the keyword measurement: a violation list alone cannot show that a FREE window is
/// empty, because "empty" produces no entries anywhere.
/// </summary>
/// <param name="Employee">Employee the window belongs to</param>
/// <param name="Keyword">Keyword in force throughout the window</param>
/// <param name="From">First day of the window</param>
/// <param name="Until">Last day of the window</param>
/// <param name="AssignmentsInWindow">Every assignment of that employee inside the window, by date</param>
public sealed record ScheduleCommandWindow(
    string Employee,
    ScheduleCommandKeyword Keyword,
    DateOnly From,
    DateOnly Until,
    IReadOnlyList<ScheduleCommandWindowAssignment> AssignmentsInWindow);
