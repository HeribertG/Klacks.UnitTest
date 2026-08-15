// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// What the plan made of the booked absences — the metric family scenario 6 adds. Every field is empty
/// or neutral for a scenario without absences, so scenarios 1 to 5 measure exactly what they measured
/// before.
/// </summary>
/// <param name="Entries">Every absence window of every employee, in list-rank and date order</param>
/// <param name="Rows">Absence rows the scenario declares; one row is one employee on one day</param>
/// <param name="Violations">
/// Assignments on an absence day. MUST be empty: the block is a hard veto in seeding, auction,
/// evolution and the theoretical maximum alike, so an entry is a bypassed veto
/// </param>
/// <param name="BoundaryCases">
/// Assignments overlapping an absence window with one of their two ends, with the end that lies
/// inside and whether the plan accepted them
/// </param>
/// <param name="DaysCountedAsFree">
/// True when at least one absence day was counted as a free day somewhere in the package measures.
/// MUST be false: an absence day is blocked and paid, a free day is neither
/// </param>
/// <param name="RedundantWithKeyword">Days on which an absence and a schedule command both apply</param>
/// <param name="CreditHoursByEmployee">Hours the absences credit to the hour target, per absent employee</param>
public sealed record AbsenceMetrics(
    IReadOnlyList<AbsenceWindowEntry> Entries,
    int Rows,
    IReadOnlyList<AbsenceViolation> Violations,
    IReadOnlyList<AbsenceBoundaryCase> BoundaryCases,
    bool DaysCountedAsFree,
    IReadOnlyList<AbsenceKeywordRedundancy> RedundantWithKeyword,
    IReadOnlyDictionary<string, double> CreditHoursByEmployee)
{
    /// <summary>The measurement of a scenario that books no absence at all.</summary>
    public static AbsenceMetrics Empty { get; } = new(
        [], 0, [], [], false, [], new SortedDictionary<string, double>(StringComparer.Ordinal));

    /// <summary>True when the scenario books at least one absence day.</summary>
    public bool IsActive => Rows > 0;
}
