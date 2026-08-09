// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Fixtures;

/// <summary>
/// What the fixture knows about WHY one employee is banned from one shift: the name of the keyword
/// the employee is missing and, when the fixture models a temporal restriction, the validity window
/// of the employee's keyword assignment. The engine itself has no keyword model — its ban list is a
/// bare (agent, shift, date) set — so this description exists purely so the analyzer can report a
/// violation with the fields the specification asks for instead of an anonymous triple.
/// </summary>
/// <param name="MissingKeyword">Name of the keyword the employee lacks for the shift, for example NACHT-BEF</param>
/// <param name="ValidFrom">First day the employee's keyword assignment was valid; null when it never existed or is open</param>
/// <param name="ValidUntil">Last day the employee's keyword assignment was valid; null when it never existed or is open</param>
public sealed record AutofillKeywordFact(
    string MissingKeyword,
    DateOnly? ValidFrom,
    DateOnly? ValidUntil);
