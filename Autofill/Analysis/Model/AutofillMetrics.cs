// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// The full measurement of one autofill plan — the object the specification lists under "Metriken",
/// plus the engine's own fitness stages and free-text notes for anything the analyzer could not
/// determine. Scenario-independent: nothing here knows which scenario produced the plan.
/// </summary>
/// <param name="Scenario">Scenario name, used as the artifact folder</param>
/// <param name="TestName">Test name, used as the artifact file name</param>
/// <param name="RunLabel">Which run this is: run1, run2 or the auction seed plan</param>
/// <param name="PeriodFrom">First day of the planning period</param>
/// <param name="PeriodUntil">Last day of the planning period</param>
/// <param name="Coverage">Rule 1</param>
/// <param name="Legality">Rule 2</param>
/// <param name="Packages">Rules 3 and 4</param>
/// <param name="Rotation">Rule 5</param>
/// <param name="Hours">Rule 6</param>
/// <param name="Fairness">Rule 7</param>
/// <param name="Eligibility">Input-side pools from the fixture ban list; empty lists without one</param>
/// <param name="Keyword">Independent ban-list scan of the finished plan; empty without a ban list</param>
/// <param name="CarryIn">Continuation of the previous month; empty for a clean start</param>
/// <param name="Determinism">Filled by the deterministic runner after the second run</param>
/// <param name="Fitness">The engine's own stage scores</param>
/// <param name="Notes">Everything the analyzer could not decide, in plain words</param>
public sealed record AutofillMetrics(
    string Scenario,
    string TestName,
    string RunLabel,
    DateOnly PeriodFrom,
    DateOnly PeriodUntil,
    CoverageMetrics Coverage,
    LegalityMetrics Legality,
    PackageMetrics Packages,
    RotationMetrics Rotation,
    HoursMetrics Hours,
    FairnessMetrics Fairness,
    EligibilityMetrics Eligibility,
    KeywordMetrics Keyword,
    IReadOnlyList<CarryInRespect> CarryIn,
    DeterminismMetrics Determinism,
    EngineFitness Fitness,
    IReadOnlyList<string> Notes)
{
    /// <summary>Rule 10: order loyalty inside and across packages. Empty without an order dimension.</summary>
    public OrderMetrics Orders { get; init; } = OrderMetrics.Empty;

    /// <summary>
    /// Continuation of the previous month judged on order, shift kind and remaining length together.
    /// Empty for a clean start and for a scenario without an order dimension.
    /// </summary>
    public IReadOnlyList<CarryInOrderRespect> CarryInThreeDimensional { get; init; } = [];

    /// <summary>Filled by the runner, which is the only part of the suite that owns a clock.</summary>
    public RuntimeMetrics Runtime { get; init; } = RuntimeMetrics.NotMeasured;

    /// <summary>
    /// The day-shift view: which assignments staff a day shift and how the day shifts spread over the
    /// roster. Empty for a scenario that cuts none.
    /// </summary>
    public DayShiftMetrics DayShift { get; init; } = DayShiftMetrics.Empty;

    /// <summary>
    /// What the plan made of the master-data shift preferences. Empty for a scenario without any.
    /// </summary>
    public PreferenceMetrics Preferences { get; init; } = PreferenceMetrics.Empty;

    /// <summary>
    /// What the plan made of the booked absences. Empty for a scenario without any, so every scenario
    /// before scenario 6 measures exactly what it measured before.
    /// </summary>
    public AbsenceMetrics Absences { get; init; } = AbsenceMetrics.Empty;

    /// <summary>
    /// The capacity arithmetic of the fixture — computable before the run and therefore never a
    /// verdict about the algorithm.
    /// </summary>
    public AvailabilityMetrics Availability { get; init; } = AvailabilityMetrics.Empty;

    /// <summary>
    /// Comparison against a run that finished earlier and whose measurement was stored. Filled by the
    /// scenario test, not by the analyzer, because the analyzer sees one plan at a time.
    /// </summary>
    public BaselineDiff DiffVsBaseline { get; init; } = BaselineDiff.None;

    /// <summary>
    /// Every in-period assignment, so a later run can diff against this artifact slot by slot without
    /// re-running the engine that produced it. Added 2026-08-14; artifacts written before that day do
    /// not carry it, and a reader must report that instead of diffing against an empty list.
    /// </summary>
    public IReadOnlyList<PlanAssignmentRow> Assignments { get; init; } = [];
}
