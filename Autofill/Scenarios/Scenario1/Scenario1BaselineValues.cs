// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario1;

/// <summary>
/// The no-regression floor of scenario 1 (clean start, 180 guaranteed hours). Every constant below is
/// a value that was actually measured, not a value that is wanted: the specification assertions A4 to
/// A8 are red here, and these pins exist so the red state cannot quietly get redder while the engine
/// is rebuilt.
/// <para>
/// Each constant is a band edge, not a single measurement — the worst of what the engine produced
/// under the seeds of <see cref="AutofillSeedBand.Seeds"/>. The reference state is the engine at
/// commit 3efe150 plus the uncommitted operator rework in
/// <c>Klacks.ScheduleOptimizer/TokenEvolution/Operators/</c> (BlockCrossover, TokenSwapMutation),
/// measured 2026-08-08. That rework removed every package above five days and cost forward rotation;
/// both effects are pinned here on purpose.
/// </para>
/// <para>
/// Re-pin them deliberately — after an improvement, with a new band measurement over the same seeds
/// and a new engine reference — never to make a failing test pass. The band artifact
/// <c>artifacts/scenario1/Scenario1CleanStart.band.json</c> is written on every run and reports the
/// values to copy under "Worst".
/// </para>
/// </summary>
public static class Scenario1BaselineValues
{
    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-08 on engine 3efe150 + uncommitted P2 operators:
    /// 0.3333/0.375/0.3333, so the floor is 0.3333. Seed 42 runs 8 of 24 package transitions forward.
    /// </summary>
    private const double ForwardRate = 0.3333333333333333;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-08 on engine 3efe150 + uncommitted P2 operators:
    /// 18/18/18, so the ceiling is 18. Seed 42 mixes shift kinds in 18 of 29 packages.
    /// </summary>
    private const int MixedTypeCount = 18;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-08 on engine 3efe150 + uncommitted P2 operators:
    /// 0.3448/0.3103/0.3448, so the ceiling is 0.3448. Seed 42 has 10 of 29 packages of at most two days.
    /// </summary>
    private const double ShortPackageShare = 0.3448275862068966;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-08 on engine 3efe150 + uncommitted P2 operators:
    /// 0/0/0. Removing the overlong packages is what the operator rework was for, so this pin stays at
    /// zero and is meant to be sharp rather than tolerant.
    /// </summary>
    private const int PackagesOverIdealLength = 0;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-08 on engine 3efe150 + uncommitted P2 operators:
    /// 0.1034 on all three seeds. Seed 42 has 3 of 29 packages of five days followed by exactly two
    /// free days.
    /// </summary>
    private const double IdealShare = 0.10344827586206896;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-08 on engine 3efe150 + uncommitted P2 operators:
    /// 8/8/8. Seed 42 spreads early 7, late 5, night 8 over all five employees.
    /// </summary>
    private const int ShiftKindSpread = 8;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-08 on engine 3efe150 + uncommitted P2 operators:
    /// 1/1/1. On seed 42 rank 4 reaches a higher fulfilment than rank 3.
    /// </summary>
    private const int MonotonicityViolations = 1;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-08 on engine 3efe150 + uncommitted P2 operators:
    /// 616/616/624 h, so the floor is 616 h. Seed 42 gives rank 1 184 h, rank 2 160 h, rank 3 128 h
    /// and rank 4 144 h, while rank 5 holds 128 h — the operator rework pushed 64 h down to the last
    /// rank, and rule 5 ranks the top-down service of the guaranteed hours above rule 6, package
    /// integrity, so that shift is a regression at a high priority. It is pinned here to stop it going
    /// further.
    /// </summary>
    private const double TopRanksPlannedHours = 616;

    /// <summary>The floor scenario 1 must not fall below.</summary>
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
