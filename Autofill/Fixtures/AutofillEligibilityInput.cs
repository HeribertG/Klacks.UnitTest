// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.Autofill.Fixtures;

/// <summary>
/// The eligibility knowledge a scenario hands to BOTH sides of a run: the ban list goes into
/// <c>CoreWizardContext.IneligibleAssignments</c> — the engine's only eligibility field, a bare
/// (agent, shift, date) set with the temporal semantics already resolved to days at the API border —
/// and the same object goes onto the scenario definition so the analyzer can measure pools, keyword
/// violations, forced rotations and forced shortenings against exactly the restriction the engine saw.
/// Building both from one instance is deliberate: a test can then never assert against a different
/// ban list than the one it planned with.
/// <para>
/// The shift-kind map exists because the ban list speaks in shift ids while every metric of the suite
/// speaks in shift kinds; the analyzer treats this map as the authority instead of re-deriving kinds
/// from time spans, so a deliberately unusual fixture time still measures under the kind the fixture
/// declares. <see cref="ValidationProblems"/> checks that the map covers every shift id the ban list
/// or the engine input mentions and that every banned agent exists — the scenario builder and the
/// analyzer both fail loudly on a mismatch rather than silently measuring an empty pool.
/// </para>
/// <para>
/// Keyword facts are OPTIONAL per (agent, shift) pair, not per day triple: a fixture restriction is
/// naturally "this employee lacks keyword X for this shift (in this window)" and the day triples are
/// derived from it. A banned triple without a fact is reported under the uniform placeholder
/// <see cref="UnspecifiedKeywordPlaceholder"/> — chosen over failing, because on engine level the
/// keyword name is fixture documentation, not engine input, and a missing name must never hide a
/// real violation.
/// </para>
/// </summary>
/// <param name="IneligibleAssignments">Banned (agent, shift, date) triples, same format and instance the engine context receives</param>
/// <param name="ShiftKindsById">Authoritative map from a shift id to the kind the suite files it under</param>
public sealed record AutofillEligibilityInput(
    IReadOnlySet<(string AgentId, Guid ShiftId, DateOnly Date)> IneligibleAssignments,
    IReadOnlyDictionary<Guid, AutofillShiftKind> ShiftKindsById)
{
    /// <summary>Reported as the missing keyword of a banned triple the fixture gave no fact for.</summary>
    public const string UnspecifiedKeywordPlaceholder = "UNSPECIFIED-KEYWORD";

    /// <summary>
    /// Why each (agent, shift) pair is banned, in fixture terms. Keys must match the ban triples;
    /// pairs without an entry fall back to <see cref="UnspecifiedKeywordPlaceholder"/>.
    /// </summary>
    public IReadOnlyDictionary<(string AgentId, Guid ShiftId), AutofillKeywordFact> KeywordFacts { get; init; }
        = new Dictionary<(string AgentId, Guid ShiftId), AutofillKeywordFact>();

    /// <summary>
    /// The fact behind a banned pair, or the placeholder fact when the fixture supplied none.
    /// </summary>
    /// <param name="agentId">Employee identifier</param>
    /// <param name="shiftId">Shift the employee is banned from</param>
    public AutofillKeywordFact FactOf(string agentId, Guid shiftId)
        => KeywordFacts.TryGetValue((agentId, shiftId), out var fact)
            ? fact
            : new AutofillKeywordFact(UnspecifiedKeywordPlaceholder, null, null);

    /// <summary>
    /// Enumerates one ban triple per day of an inclusive date range — the helper that turns a fixture
    /// statement like "MA-2 may not hold the night shift from 21.03. to 31.03." into the day triples
    /// the engine needs, because the temporal validity of a keyword does not exist on engine level.
    /// </summary>
    /// <param name="agentId">Employee identifier</param>
    /// <param name="shiftId">Shift the employee is banned from</param>
    /// <param name="from">First banned day, inclusive</param>
    /// <param name="until">Last banned day, inclusive</param>
    public static IEnumerable<(string AgentId, Guid ShiftId, DateOnly Date)> BanTriples(
        string agentId, Guid shiftId, DateOnly from, DateOnly until)
    {
        for (var date = from; date <= until; date = date.AddDays(1))
        {
            yield return (agentId, shiftId, date);
        }
    }

    /// <summary>
    /// Everything about this input that would make a measurement silently wrong: a banned agent the
    /// scenario does not contain, a banned shift id or an engine shift id the kind map does not cover.
    /// Empty means the input is usable. Callers throw on any entry; the check itself does not throw
    /// so both callers can report all problems at once.
    /// </summary>
    /// <param name="context">Engine input the ban list will run against</param>
    /// <param name="employeesInListOrder">Employees of the scenario</param>
    public IReadOnlyList<string> ValidationProblems(
        CoreWizardContext context, IReadOnlyList<string> employeesInListOrder)
    {
        var problems = new List<string>();

        foreach (var agentId in IneligibleAssignments
                     .Select(b => b.AgentId)
                     .Distinct()
                     .Where(a => !employeesInListOrder.Contains(a, StringComparer.Ordinal))
                     .OrderBy(a => a, StringComparer.Ordinal))
        {
            problems.Add($"The ban list names employee '{agentId}', who is not part of the scenario.");
        }

        foreach (var shiftId in IneligibleAssignments
                     .Select(b => b.ShiftId)
                     .Distinct()
                     .Where(id => !ShiftKindsById.ContainsKey(id))
                     .OrderBy(id => id))
        {
            problems.Add($"The ban list names shift id '{shiftId}', which the shift-kind map does not cover.");
        }

        foreach (var shiftId in context.Shifts
                     .Select(s => Guid.TryParse(s.Id, out var id) ? id : Guid.Empty)
                     .Distinct()
                     .Where(id => !ShiftKindsById.ContainsKey(id))
                     .OrderBy(id => id))
        {
            problems.Add($"The engine input contains shift id '{shiftId}', which the shift-kind map does not cover.");
        }

        return problems;
    }
}
