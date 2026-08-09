// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario2;

/// <summary>
/// The no-regression floor of scenario 2 (carry-in out of February 2026). All values were measured
/// after the analyzer began building packages across the month boundary, so they describe whole
/// packages, not packages cut off at 2026-03-01; the pre-correction numbers are not comparable and
/// must not be used to re-pin these.
/// <para>
/// Each constant is a band edge, not a single measurement — the worst of what the engine produced
/// under the seeds of <see cref="AutofillSeedBand.Seeds"/>. The band artifact
/// <c>artifacts/scenario2/Scenario2.band.json</c> is written on every run and reports the values to
/// copy under "Worst".
/// </para>
/// <para>
/// Re-measured 2026-08-09 on engine state E1 of the scenario-4 fix plan — the carried-in package is
/// now CONSTRUCTED before the free assignment (<c>CarryInContinuationSeeder</c>), an occupied slot is
/// no longer auctioned a second time, and the stage-1 block-length check counts the previous month
/// like every other block rule does. Scenario 2 is the small carry-in reference, so it is the
/// scenario this rework moves most; the previous band was measured 2026-08-08 on engine 3efe150 plus
/// the P2 operator rework. Six of the nine edges improved and were tightened, two got worse and were
/// lowered with the reason stated on the constant, one is unchanged. Scenario 1 and 1b did not move
/// at all — without an open package the construction and its fitness term are a strict no-op — and
/// their pins are therefore untouched.
/// </para>
/// </summary>
public static class Scenario2BaselineValues
{
    /// <summary>
    /// Band over seeds 42/43/44, re-measured 2026-08-09 on engine state E1: 0.375/0.36/0.36, so the
    /// floor is 0.36. LOWERED from 0.3846 (band 0.3846/0.3846/0.44 of 2026-08-08). The drop is the
    /// price of A10/A11: the first package of MA-1, MA-2 and MA-3 is now the carried-in one, whose
    /// shift kind February fixed, so that transition is decided by the construction and not by the
    /// rotation rule any more. The transition count itself falls with it (26 to 24 on seed 42).
    /// Raising the forward rate again is the subject of stage E4, not of E1.
    /// </summary>
    private const double ForwardRate = 0.36;

    /// <summary>
    /// Band over seeds 42/43/44, re-measured 2026-08-09 on engine state E1: 15/15/15, so the ceiling
    /// is 15. Tightened from 19 (band 19/19/18 of 2026-08-08): a constructed package is pure by
    /// construction, so four packages fewer mix kinds.
    /// </summary>
    private const int MixedTypeCount = 15;

    /// <summary>
    /// Band over seeds 42/43/44, re-measured 2026-08-09 on engine state E1: 0.2759/0.3/0.3, so the
    /// ceiling is 0.3. Tightened from 0.3548 (band 0.3226/0.3548/0.3333 of 2026-08-08).
    /// </summary>
    private const double ShortPackageShare = 0.3;

    /// <summary>
    /// Band over seeds 42/43/44, re-measured 2026-08-09 on engine state E1: 0/0/0 — unchanged, and
    /// the pin stays sharp at zero. It took the stage-1 fix to keep it there: with the package
    /// constructed to its full five days the auction sat exactly on the cap, and stage 1 was the only
    /// block-length check in the engine that counted the in-period days alone, so it waved a sixth
    /// day through. With the previous month counted the value is zero on every seed again.
    /// </summary>
    private const int PackagesOverIdealLength = 0;

    /// <summary>
    /// Band over seeds 42/43/44, re-measured 2026-08-09 on engine state E1: 0.2414/0.2/0.2, so the
    /// floor is 0.2. Tightened from 0.1613 (band 0.1613/0.1935/0.2 of 2026-08-08).
    /// </summary>
    private const double IdealShare = 0.2;

    /// <summary>
    /// Band over seeds 42/43/44, re-measured 2026-08-09 on engine state E1: 5/5/5, so the ceiling is
    /// 5. Tightened from 9 (band 9/9/9 of 2026-08-08) — the largest single improvement of the stage.
    /// </summary>
    private const int ShiftKindSpread = 5;

    /// <summary>
    /// Band over seeds 42/43/44, re-measured 2026-08-09 on engine state E1: 0/1/1, so the ceiling is
    /// 1. Tightened from 2 (band 2/1/0 of 2026-08-08).
    /// </summary>
    private const int MonotonicityViolations = 1;

    /// <summary>
    /// Band over seeds 42/43/44, re-measured 2026-08-09 on engine state E1: 4/4/4, so the floor is 4.
    /// RAISED from 1 (band 1/1/1 of 2026-08-08). Four of the five carried-in packages are now
    /// continued as the specification asks — the three open ones plus MA-5, whose closed late package
    /// rotated to night. The fifth is MA-4, which belongs to A13 and to the boundary rotation, not to
    /// this stage. Raising the floor is what stops a later stage from undoing the construction
    /// unnoticed.
    /// </summary>
    public const int CarryInOkCount = 4;

    /// <summary>
    /// Band over seeds 42/43/44, re-measured 2026-08-09 on engine state E1: 632/616/616 h, so the
    /// floor is 616 h. LOWERED from 640 h (band 640/648/656 of 2026-08-08) — one shift of eight hours
    /// on seed 42, three on seeds 43 and 44. Cause: MA-2 closes its carried-in package on 1 March and
    /// the two contractual rest days then keep it off 2 and 3 March, days it used to work; the hours
    /// land on rank 5. The same run is more monotone than before (violations 2 to 0 on seed 42), so
    /// the top-down ORDER improved while the top-down SUM fell. This is the one edge of stage E1 that
    /// trades a rule of the specification against a pin, and the owner decision B1 ranks the carry-in
    /// continuation as a must-fix; the number is reported so the trade stays visible.
    /// </summary>
    private const double TopRanksPlannedHours = 616;

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
