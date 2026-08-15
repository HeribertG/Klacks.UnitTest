// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// The control-group comparison of two runs with every changed slot attributed to a cause: the
/// treated run carries preferences and commands, the control run does not, and the question is whether
/// every difference between them traces back to one of those two inputs.
/// </summary>
/// <param name="ChangedAssignments">Every slot the two runs staff differently, with its cause</param>
/// <param name="UnexplainedCount">How many of them the rule could not attribute</param>
/// <param name="AttributionRule">
/// The rule text, written into the artifact so a reader can judge the attribution instead of trusting
/// the count. It is stated once and never widened to reach zero — a rule generous enough to explain
/// everything measures nothing
/// </param>
public sealed record AttributedAssignmentDiff(
    IReadOnlyList<AttributedChangedAssignment> ChangedAssignments,
    int UnexplainedCount,
    string AttributionRule)
{
    /// <summary>Changed slots grouped by the cause they were attributed to, most frequent first.</summary>
    public IReadOnlyList<(DiffAttribution Cause, int Count)> CountByCause
        => ChangedAssignments
            .GroupBy(c => c.AttributedTo)
            .Select(g => (Cause: g.Key, Count: g.Count()))
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.Cause)
            .ToList();
}
