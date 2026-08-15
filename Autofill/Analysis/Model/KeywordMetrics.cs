// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// Keyword violations of the finished plan, computed by an INDEPENDENT scan of every planned and
/// carried-in shift against the fixture ban list — never from the engine's ConstraintViolation list,
/// which skips locked assignments and would therefore silently exempt exactly the carry-in stock
/// (finding K8). Only the violation list exists on engine level: the specification's boundaryCases
/// and irrelevantKeywordInfluence require the API's validity evaluation and its keyword catalogue,
/// which the engine's bare ban list has already flattened away (findings K3 and K4), so they are
/// measured one level up against the EligibilityMatrixBuilder and not here.
/// </summary>
/// <param name="Violations">Every planned or carried-in shift the ban list forbids, ordered by list rank, date and kind</param>
public sealed record KeywordMetrics(
    IReadOnlyList<KeywordViolation> Violations)
{
    /// <summary>
    /// Violations of the engine's own per-day SCHEDULE COMMANDS — Free, OnlyEarly, OnlyLate,
    /// OnlyNight and their negations — which are a different mechanism from the qualification ban list
    /// above and share only the word "keyword".
    /// <para>
    /// Deliberately NOT merged into <see cref="Violations"/> and deliberately not named "violations",
    /// although the scenario-5 specification writes the metric as <c>keyword.violations[]</c>. That
    /// name was already taken by the scenario-3 ban-list scan, whose shape is different and whose
    /// artifacts scenarios 1 to 4 pin; reusing it would have changed an existing measurement to make a
    /// new one fit a name.
    /// </para>
    /// </summary>
    public IReadOnlyList<ScheduleCommandViolation> ScheduleCommandViolations { get; init; } = [];

    /// <summary>
    /// Every command window of the fixture with the assignments the plan placed inside it. This is
    /// what makes an EMPTY window checkable: a FREE window that was honoured produces no violation and
    /// no assignment, and only a window list can tell that apart from a window that was never applied.
    /// </summary>
    public IReadOnlyList<ScheduleCommandWindow> Windows { get; init; } = [];
}
