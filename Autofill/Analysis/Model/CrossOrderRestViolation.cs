// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// A rest-time violation whose two shifts belong to different orders. A subset of the plain rest
/// violations, kept apart because it answers the scenario-4 question directly: whether the rest check
/// still sees an employee once the employee's day is spread over several parallel orders.
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="DateFrom">Day of the earlier shift</param>
/// <param name="DateTo">Day of the later shift</param>
/// <param name="FromOrder">Order of the earlier shift</param>
/// <param name="ToOrder">Order of the later shift</param>
/// <param name="FromShift">Kind of the earlier shift</param>
/// <param name="ToShift">Kind of the later shift</param>
/// <param name="GapHours">Hours between the end of the earlier and the start of the later shift</param>
public sealed record CrossOrderRestViolation(
    string Employee,
    DateOnly DateFrom,
    DateOnly DateTo,
    int FromOrder,
    int ToOrder,
    AutofillShiftKind FromShift,
    AutofillShiftKind ToShift,
    double GapHours);
