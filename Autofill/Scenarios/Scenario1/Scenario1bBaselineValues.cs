// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario1;

/// <summary>
/// The no-regression floor of calibration variant 1b (clean start, 150 guaranteed hours). Because
/// every goal is satisfiable at the same time in this variant, its floor is the more sensitive of the
/// two clean-start pins: a change that only looks harmless because scenario 1 is structurally
/// undersupplied will show up here first.
/// <para>
/// Each constant is a band edge, not a single measurement — the worst of what the engine produced
/// under the seeds of <see cref="AutofillSeedBand.Seeds"/>. The reference state is the engine at
/// commit 3efe150 plus the then uncommitted operator rework in
/// <c>Klacks.ScheduleOptimizer/TokenEvolution/Operators/</c> (BlockCrossover, TokenSwapMutation),
/// measured 2026-08-08; that rework is now committed as 4530293. On it the band of this variant runs
/// wider than two of the pins allowed for, so <see cref="AutofillBaseline.MaxShortPackageShare"/> and
/// <see cref="AutofillBaseline.MaxMixedTypeCount"/> were re-measured and re-pinned on 2026-08-08
/// after P5. The other six still carry the earlier measurement, which the current band fits inside.
/// The band
/// artifact <c>artifacts/scenario1b/Scenario1bCalibration.band.json</c> is written on every run and
/// reports the values to copy under "Worst".
/// </para>
/// </summary>
public static class Scenario1bBaselineValues
{
    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-08 on engine 3efe150 + uncommitted P2 operators:
    /// 0.36/0.32/0.36, so the floor is 0.32. Seed 42 runs 9 of 25 package transitions forward.
    /// </summary>
    private const double ForwardRate = 0.32;

    /// <summary>
    /// Band over seeds 42/43/44, re-pinned 2026-08-08 after P5, engine 4530293: 16/17/18, so the
    /// ceiling is 18 and seed 44 sets it. The asserted run on seed 42 mixes shift kinds in 16 of 30
    /// packages. The old ceiling of 17 no longer bounded the band the engine covers.
    /// </summary>
    private const int MixedTypeCount = 18;

    /// <summary>
    /// Band over seeds 42/43/44, re-pinned 2026-08-08 after P5, engine 4530293: 0.3667/0.3667/0.4, so
    /// the ceiling is 0.4 and seed 44 sets it. The asserted run on seed 42 has 11 of 30 packages of at
    /// most two days. The old ceiling of 0.3667 no longer bounded the band the engine covers.
    /// </summary>
    private const double ShortPackageShare = 0.4;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-08 on engine 3efe150 + uncommitted P2 operators:
    /// 0/0/0. Removing the overlong packages is what the operator rework was for, so this pin stays at
    /// zero and is meant to be sharp rather than tolerant.
    /// </summary>
    private const int PackagesOverIdealLength = 0;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-08 on engine 3efe150 + uncommitted P2 operators:
    /// 0.0667 on all three seeds. Seed 42 has 2 of 30 packages of five days followed by exactly two
    /// free days.
    /// </summary>
    private const double IdealShare = 0.06666666666666667;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-08 on engine 3efe150 + uncommitted P2 operators:
    /// 9/10/9, so the ceiling is 10. Seed 42 spreads early 6, late 6, night 9 over all five employees.
    /// </summary>
    private const int ShiftKindSpread = 10;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-08 on engine 3efe150 + uncommitted P2 operators:
    /// 1/1/0, so the ceiling is 1. On seed 42 rank 4 reaches a higher fulfilment than rank 3, on seed
    /// 44 the order holds — the clearest case in the suite of a metric the seed alone decides.
    /// </summary>
    private const int MonotonicityViolations = 1;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-08 on engine 3efe150 + uncommitted P2 operators:
    /// 600/592/600 h, so the floor is 592 h. Seed 42 gives ranks 1 to 4 152/152/144/152 h and rank 5
    /// 144 h. Rule 5 ranks the top-down service of the guaranteed hours above rule 6, package
    /// integrity, so hours moving from these four down the list is a regression even when nothing else
    /// gets worse.
    /// </summary>
    private const double TopRanksPlannedHours = 592;

    /// <summary>The floor variant 1b must not fall below.</summary>
    public static AutofillBaseline Baseline { get; } = new(
        MinForwardRate: ForwardRate,
        MaxMixedTypeCount: MixedTypeCount,
        MaxShortPackageShare: ShortPackageShare,
        MaxPackagesOverIdealLength: PackagesOverIdealLength,
        MinIdealShare: IdealShare,
        MaxShiftKindSpread: ShiftKindSpread,
        MaxMonotonicityViolations: MonotonicityViolations,
        MinTopRanksPlannedHours: TopRanksPlannedHours);
}
