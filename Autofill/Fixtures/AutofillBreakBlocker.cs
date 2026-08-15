// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.Autofill.Fixtures;

/// <summary>
/// ONE booked absence day of one employee — the fixture's mirror of a single <c>Break</c> row.
/// <para>
/// It carries a single date and never a window, because the production entity does not have one:
/// <c>ScheduleEntryBase</c> holds only <c>CurrentDate</c>, and
/// <c>WizardHardConstraintBuilder.cs:108-115</c> turns every such row into
/// <c>new CoreBreakBlocker(clientId, b.CurrentDate, b.CurrentDate, reason, b.WorkTime)</c> — a blocker
/// whose start and end are the same day. Two weeks of holiday are therefore fourteen of these, which is
/// owner decision O2 of 2026-08-14.
/// </para>
/// <para>
/// Why the one-day shape is not a detail. <c>TokenFitnessEvaluator.ComputeBreakHoursByAgent</c>
/// multiplies <c>Hours</c> by the number of days the blocker spans (<c>:274-275</c>). In production
/// that factor is always 1; a hand-built range blocker would silently multiply the credit and pin the
/// hour assertion to a code path production never reaches. Keeping the row at one day keeps the
/// multiplication inert exactly as it is in production.
/// </para>
/// </summary>
/// <param name="AgentId">Employee the absence belongs to</param>
/// <param name="Date">The single calendar day the absence covers</param>
/// <param name="Reason">Absence name, carried to the engine for diagnostics only</param>
/// <param name="Hours">
/// Paid hours of this day (<c>Break.WorkTime</c>). It is credited to the hour target and never to the
/// weekly cap. A row with 0 h still blocks the day: the <c>Hours &lt;= 0</c> skip in
/// <c>ComputeBreakHoursByAgent</c> (<c>:262</c>) drops the CREDIT, while the blocking predicate
/// <c>IsBlockedByBreak</c> never looks at the hours at all
/// </param>
public sealed record AutofillBreakBlocker(
    string AgentId,
    DateOnly Date,
    string Reason,
    decimal Hours)
{
    /// <summary>The engine row behind this day: a blocker whose start and end are this very date.</summary>
    public CoreBreakBlocker ToCore() => new(AgentId, Date, Date, Reason, Hours);

    /// <summary>True when this row grants hours towards the target; false for a pure lock day.</summary>
    public bool GrantsCredit => Hours > 0m;
}
