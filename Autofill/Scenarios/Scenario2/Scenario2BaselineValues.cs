// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario2;

/// <summary>
/// The no-regression floor of scenario 2 (carry-in out of February 2026). All values were measured
/// after the analyzer began building packages across the month boundary, so they describe whole
/// packages, not packages cut off at 2026-03-01; the pre-correction numbers are not comparable and
/// must not be used to re-pin these.
/// <para>
/// Each constant is a band edge, not a single measurement — the worst of what the engine produced
/// under the seeds of <see cref="AutofillSeedBand.Seeds"/>. The reference state is the engine at
/// commit 3efe150 plus the uncommitted operator rework in
/// <c>Klacks.ScheduleOptimizer/TokenEvolution/Operators/</c> (BlockCrossover, TokenSwapMutation),
/// measured 2026-08-08. The band artifact <c>artifacts/scenario2/Scenario2.band.json</c> is written on
/// every run and reports the values to copy under "Worst".
/// </para>
/// </summary>
public static class Scenario2BaselineValues
{
    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-08 on engine 3efe150 + uncommitted P2 operators:
    /// 0.3846/0.3846/0.44, so the floor is 0.3846. Seed 42 runs 10 of 26 package transitions forward.
    /// </summary>
    private const double ForwardRate = 0.38461538461538464;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-08 on engine 3efe150 + uncommitted P2 operators:
    /// 19/19/18, so the ceiling is 19. Seed 42 mixes shift kinds in 19 of 31 packages.
    /// </summary>
    private const int MixedTypeCount = 19;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-08 on engine 3efe150 + uncommitted P2 operators:
    /// 0.3226/0.3548/0.3333, so the ceiling is 0.3548. Seed 42 has 10 of 31 packages of at most two
    /// days, which is below the ceiling — the band is what carries the difference.
    /// </summary>
    private const double ShortPackageShare = 0.3548387096774194;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-08 on engine 3efe150 + uncommitted P2 operators:
    /// 0/0/0. Even with a package spanning the month boundary no package exceeds five days any more,
    /// so the pin stays at zero and is meant to be sharp rather than tolerant.
    /// </summary>
    private const int PackagesOverIdealLength = 0;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-08 on engine 3efe150 + uncommitted P2 operators:
    /// 0.1613/0.1935/0.2, so the floor is 0.1613. Seed 42 has 5 of 31 packages of five days followed
    /// by exactly two free days.
    /// </summary>
    private const double IdealShare = 0.16129032258064516;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-08 on engine 3efe150 + uncommitted P2 operators:
    /// 9/9/9. Seed 42 spreads early 9, late 6, night 4 over all five employees.
    /// </summary>
    private const int ShiftKindSpread = 9;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-08 on engine 3efe150 + uncommitted P2 operators:
    /// 2/1/0, so the ceiling is 2. This is the widest band in the suite: on seed 44 the fulfilment
    /// never rises against the list order, on seed 42 it does twice, and nothing but the seed differs.
    /// </summary>
    private const int MonotonicityViolations = 2;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-08 on engine 3efe150 + uncommitted P2 operators:
    /// 1/1/1. Exactly one of the five carried-in packages is continued as the specification requires —
    /// MA-5, whose closed late package rotated to night. The other four are the substance of A10, A11
    /// and A13; this floor only stops the one that works from disappearing unnoticed.
    /// </summary>
    public const int CarryInOkCount = 1;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-08 on engine 3efe150 + uncommitted P2 operators:
    /// 640/648/656 h, so the floor is 640 h. Seed 42 gives ranks 1 to 4 168/176/144/152 h and rank 5
    /// 104 h. Rule 5 ranks the top-down service of the guaranteed hours above rule 6, package
    /// integrity, so hours moving from these four down the list is a regression even when nothing else
    /// gets worse.
    /// </summary>
    private const double TopRanksPlannedHours = 640;

    /// <summary>The floor scenario 2 must not fall below.</summary>
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
