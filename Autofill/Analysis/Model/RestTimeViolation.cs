// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// Two shifts of one employee that lie closer together than the contractual rest time. Named
/// RestTimeViolation rather than RestViolation because the API domain already owns that name and the
/// test project imports it globally.
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="DateFrom">Day of the earlier shift</param>
/// <param name="DateTo">Day of the later shift</param>
/// <param name="FromShift">Kind of the earlier shift</param>
/// <param name="ToShift">Kind of the later shift</param>
/// <param name="GapHours">Hours between the end of the earlier and the start of the later shift; negative means they overlap</param>
public sealed record RestTimeViolation(
    string Employee,
    DateOnly DateFrom,
    DateOnly DateTo,
    AutofillShiftKind FromShift,
    AutofillShiftKind ToShift,
    double GapHours);
