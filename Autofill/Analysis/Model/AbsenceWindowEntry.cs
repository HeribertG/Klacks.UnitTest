// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// One maximal run of consecutive absence days of one employee, rebuilt from the one-day rows the
/// engine is given. The window is the unit a reader thinks in ("two weeks of holiday"); the row count
/// is reported next to it, because the row is the unit the engine and the hour credit work on.
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="ListRank">Position in the displayed list, 1 = top</param>
/// <param name="FromInclusive">First absent day</param>
/// <param name="UntilInclusive">Last absent day</param>
/// <param name="Rows">Absence rows the window consists of; equals the number of calendar days it spans</param>
/// <param name="PaidRows">Rows carrying hours; the rest block the day without paying for it</param>
/// <param name="CreditHours">Hours the window credits to the hour target</param>
/// <param name="Reason">Absence name of the window's rows; several names are joined</param>
public sealed record AbsenceWindowEntry(
    string Employee,
    int ListRank,
    DateOnly FromInclusive,
    DateOnly UntilInclusive,
    int Rows,
    int PaidRows,
    double CreditHours,
    string Reason);
