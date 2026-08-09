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
    IReadOnlyList<string> Notes);
