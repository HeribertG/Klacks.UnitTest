// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.Autofill.Analysis.Model;

namespace Klacks.UnitTest.Autofill.Support;

/// <summary>
/// Everything two identical runs plus the auction seed run produced. A scenario test asserts against
/// <see cref="Metrics"/> — which is run 1 — and uses the rest for diagnosis.
/// </summary>
/// <param name="Plan">Plan of run 1; the plan all rule assertions are made against</param>
/// <param name="Metrics">Measurement of run 1, with the determinism verdict already filled in</param>
/// <param name="SecondPlan">Plan of run 2, produced with the same input and the same seed</param>
/// <param name="SecondMetrics">Measurement of run 2</param>
/// <param name="SeedPlan">Auction seed plan, before any evolution</param>
/// <param name="SeedMetrics">Measurement of the auction seed plan; diagnosis only, never asserted</param>
/// <param name="RunsIdentical">True when both runs produced the same assignments</param>
/// <param name="FirstDifference">First difference between the runs, null when identical</param>
/// <param name="CarryInUnchanged">True when the fixed previous-month works are byte-identical after the run</param>
/// <param name="CarryInDifference">First difference in the previous-month works, null when unchanged</param>
/// <param name="ArtifactPaths">Every file written for this test</param>
public sealed record DeterministicRunResult(
    CoreScenario Plan,
    AutofillMetrics Metrics,
    CoreScenario SecondPlan,
    AutofillMetrics SecondMetrics,
    CoreScenario SeedPlan,
    AutofillMetrics SeedMetrics,
    bool RunsIdentical,
    string? FirstDifference,
    bool CarryInUnchanged,
    string? CarryInDifference,
    IReadOnlyList<string> ArtifactPaths);
