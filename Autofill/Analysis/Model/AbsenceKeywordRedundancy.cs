// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// A day on which an absence and a schedule command both apply to the same employee — two independent
/// inputs pointing at one day. The interesting case is FREE inside an absence: two hard blocks with
/// two separate vetoes and no shared predicate, which must produce one blocked day and never a
/// conflict.
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="Date">Calendar day both inputs cover</param>
/// <param name="Keyword">The command keyword that also applies on that day</param>
/// <param name="BothBlockEveryShift">True when the two inputs forbid the same thing — the redundant case</param>
public sealed record AbsenceKeywordRedundancy(
    string Employee,
    DateOnly Date,
    ScheduleCommandKeyword Keyword,
    bool BothBlockEveryShift);
