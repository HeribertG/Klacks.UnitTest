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
/// <para>
/// Re-pinned 2026-08-12 evening by owner decision 13 (SPEC.md) onto the band measured with the
/// decision-12 changes (hour-based unescalatable package rest, fairness trade). The carry-in
/// scenario is the one the rest guarantee helps most: the top-rank hours ROSE (floor 616 to 648 h)
/// and the purity improved, while the package edges pay the splintering price like everywhere else.
/// Since the SpecFirstRed cleanup (decision 11) these pins carry the whole regression protection for
/// the four measurements A4, A5, A7 and A8 used to state. Two things those assertions checked and no
/// pin covers stay documented as targets in SPEC.md: the package-length MODE (five days must be the
/// single most frequent length) and the rank-scoped shift-kind spread of ranks 1 to 4, since
/// <see cref="AutofillBaselineTestBase.Baseline_ShiftKindSpreadDidNotGrow"/> measures all employees.
/// </para>
/// </summary>
public static class Scenario2BaselineValues
{
    /// <summary>
    /// Band over seeds 42/43/44, re-measured after the calendar-package crossover of the decision-13
    /// stage: 0.3462/0.5909/0.4091, so the floor is 0.3462 and seed 42 sets it. TIGHTENED from
    /// 0.2963 — whole-package exchange rotates better. Spec target of the former A7 is 0.80 and
    /// stays documented in SPEC.md.
    /// </summary>
    private const double ForwardRate = 0.34615384615384615;

    /// <summary>
    /// Band over seeds 42/43/44, re-measured after the calendar-package crossover of the decision-13
    /// stage: 14/6/7, so the ceiling is 14. Raised from 13 by one package — the price of the calendar
    /// exchange (a transferred whole package may mix kinds), still below the pre-decision-13 ceiling
    /// of 15; the short-package ceiling tightened from 0.4063 to 0.3871 and the forward-rate floor
    /// rose from 0.2963 to 0.3462 in the same measurement. Spec target of the former A4 is 0 and
    /// stays documented in SPEC.md.
    /// </summary>
    private const int MixedTypeCount = 14;

    /// <summary>
    /// Band over seeds 42/43/44, re-measured after the calendar-package crossover of the decision-13
    /// stage: 0.3871/0.2222/0.2963, so the ceiling is 0.3871 and seed 42 sets it. TIGHTENED from
    /// 0.4063 — the splintering win of the stage.
    /// </summary>
    private const double ShortPackageShare = 0.3870967741935484;

    /// <summary>
    /// Band over seeds 42/43/44, re-measured 2026-08-09 on engine state E1: 0/0/0 — unchanged, and
    /// the pin stays sharp at zero. It took the stage-1 fix to keep it there: with the package
    /// constructed to its full five days the auction sat exactly on the cap, and stage 1 was the only
    /// block-length check in the engine that counted the in-period days alone, so it waved a sixth
    /// day through. With the previous month counted the value is zero on every seed again.
    /// </summary>
    private const int PackagesOverIdealLength = 0;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-12 evening on the decision-12 engine:
    /// 0.25/0.1852/0.1852, so the floor is 0.1852. Lowered from 0.2 (decision 13; the asserted run on
    /// seed 42 actually improved to 0.25 — eleven five-day packages — but the band edge follows the
    /// worst seed).
    /// </summary>
    private const double IdealShare = 0.18518518518518517;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-12 evening on the decision-12 engine: 6/11/12, so
    /// the ceiling is 12 and seed 44 sets it. Raised from 5 (decision 13 — the asserted run on seed 42
    /// sits at 6; the wide edge is seed variance under the rest guarantee, not a seed-42 regression).
    /// </summary>
    private const int ShiftKindSpread = 12;

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
    /// Band over seeds 42/43/44, measured 2026-08-12 evening on the decision-12 engine:
    /// 648/704/704 h, so the floor is 648 h. RAISED from 616 h — the unescalatable rest pushes hours
    /// back up the roster in this scenario (the asserted run gives ranks 1 to 4 176/168/160/144 h,
    /// rank 5 falls to 96 h), the direction the owner's rank-faithful rule asks for.
    /// </summary>
    private const double TopRanksPlannedHours = 648;

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
