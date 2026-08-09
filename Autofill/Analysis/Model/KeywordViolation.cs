// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// One assignment the finished plan holds although the ban list forbids it: the employee stands on a
/// shift for which a required keyword is missing. Keyword name and validity window come from the
/// fixture's keyword facts — the engine knows only the bare triple.
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="Date">Calendar day the shift starts on</param>
/// <param name="ShiftType">Kind of the shift</param>
/// <param name="MissingKeyword">Keyword the employee lacks; the placeholder when the fixture named none</param>
/// <param name="ValidFrom">First day the employee's keyword assignment was valid, when the fixture modelled a window</param>
/// <param name="ValidUntil">Last day the employee's keyword assignment was valid, when the fixture modelled a window</param>
public sealed record KeywordViolation(
    string Employee,
    DateOnly Date,
    AutofillShiftKind ShiftType,
    string MissingKeyword,
    DateOnly? ValidFrom,
    DateOnly? ValidUntil);
