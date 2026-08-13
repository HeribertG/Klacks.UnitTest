// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario1;

/// <summary>
/// The no-regression floor of calibration variant 1b (clean start, 150 guaranteed hours). Because
/// every goal is satisfiable at the same time in this variant, its floor is the more sensitive of the
/// two clean-start pins: a change that only looks harmless because scenario 1 is structurally
/// undersupplied will show up here first.
/// <para>
/// Since the SpecFirstRed cleanup of 2026-08-12 (SPEC.md decision 11) these pins carry the whole
/// regression protection for the four measurements A4, A5, A7 and A8 used to state. The two things
/// they checked and no pin covers — the package-length MODE and the rank-scoped shift-kind spread —
/// are documented as targets in SPEC.md; see <see cref="Scenario1BaselineValues"/>.
/// </para>
/// <para>
/// Each constant is a band edge, not a single measurement — the worst of what the engine produced
/// under the seeds of <see cref="AutofillSeedBand.Seeds"/>. Re-pinned 2026-08-12 evening by owner
/// decision 13 (SPEC.md) onto the band measured with the decision-12 changes (hour-based
/// unescalatable package rest, fairness trade): this fully-satisfiable variant pays the splintering
/// price hardest — its ideal-share floor fell to ZERO — while its rotation improved sharply. The
/// band artifact <c>artifacts/scenario1b/Scenario1bCalibration.band.json</c> is written on every run
/// and reports the values to copy under "Worst".
/// </para>
/// </summary>
public static class Scenario1bBaselineValues
{
    /// <summary>
    /// Band over seeds 42/43/44 under the decision-12b measurement rework of 2026-08-13: the
    /// rotation-bound set is empty on every seed (all package pairs are free restarts across enough
    /// rest), so the rate has no subject and the guard holds vacuously. The old floor 0.4643
    /// measured free restarts. Spec target of the former A7 is 0.80 and stays documented in SPEC.md.
    /// </summary>
    private const double ForwardRate = 0;

    /// <summary>
    /// Band over seeds 42/43/44, re-measured after the package-compactness fitness term of
    /// 2026-08-13: 23/24/23, so the ceiling is 24. Raised from 23 — consolidating short packages
    /// lets one more of them mix kinds, the same accepted trade as with the calendar exchange; the
    /// short-package ceiling tightened from 0.3824 to 0.3235 in the same measurement. Spec target
    /// of the former A4 is 0 and stays documented in SPEC.md.
    /// </summary>
    private const int MixedTypeCount = 24;

    /// <summary>
    /// Band over seeds 42/43/44 after the package-consolidation mutation of 2026-08-13:
    /// 0.2727/0.2941/0.2424, so the ceiling is 0.2941. TIGHTENED again from 0.3235 — the asserted
    /// seed (0.2727) now lies UNDER the auction-seed level of 0.3103. Spec target of the former A5
    /// is a share of at most 0.20 and stays documented in SPEC.md.
    /// </summary>
    private const double ShortPackageShare = 0.29411764705882354;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-12 on engine af5f0fa: 0/0/0 — unchanged since
    /// 2026-08-08. Removing the overlong packages is what the operator rework was for, so this pin
    /// stays at zero and is meant to be sharp rather than tolerant.
    /// </summary>
    private const int PackagesOverIdealLength = 0;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-12 evening on the decision-12 engine: 0/0/0.0303, so
    /// the floor is 0. Lowered from 0.1 (decision 13 — the harshest splintering price in the suite:
    /// the asserted run holds a SINGLE five-day package in 33 and none of them is followed by exactly
    /// two free days; winning this back is the core goal of the commissioned package-aware repair
    /// stage).
    /// </summary>
    private const double IdealShare = 0;

    /// <summary>
    /// Band over seeds 42/43/44, re-measured after the calendar-package crossover of the decision-13
    /// stage, then again after the package-compactness fitness term of 2026-08-13: 6/7/6, so the
    /// ceiling is 7 (raised from 6 by seed 43, part of the term's accepted price next to the mixed
    /// ceiling). Spec target of the former A8 is a spread of at most 2 over ranks 1 to 5; that
    /// rank-scoped reading has no pin here, because this guard covers every rank.
    /// </summary>
    private const int ShiftKindSpread = 7;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-12 on engine af5f0fa: 0/0/0, so the ceiling is 0 and
    /// the pin is sharp. Tightened from 1. CAVEAT before treating a red here as a regression: the
    /// 2026-08-08 measurement of this very variant found 1/1/0 over the same three seeds and was
    /// documented as "the clearest case in the suite of a metric the seed alone decides"; scenario 2
    /// still carries a ceiling of 1 on the same engine. A single band of three seeds measuring 0/0/0 is
    /// therefore weaker evidence of stability here than it is for the other seven pins. It is pinned
    /// sharply anyway, because A6 no longer asserts the strict order at all (it judges with a tolerance
    /// band since 2026-08-12) and this guard is the only thing left that does — but a red is a reason
    /// to re-measure the band first, not to widen the pin.
    /// </summary>
    private const int MonotonicityViolations = 0;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-12 on engine af5f0fa: 608/608/608 h, so the floor is
    /// 608 h. Raised from 592 h (band of 2026-08-08). The asserted run gives ranks 1 to 4 152 h each
    /// and rank 5 136 h. Rule 5 ranks the top-down service of the guaranteed hours above rule 6,
    /// package integrity, so hours moving from these four down the list is a regression even when
    /// nothing else gets worse.
    /// </summary>
    private const double TopRanksPlannedHours = 608;

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
