// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario1;

/// <summary>
/// The no-regression floor of scenario 1 (clean start, 180 guaranteed hours). Every constant below is
/// a value that was actually measured, not a value that is wanted: the specification targets for
/// package purity, package length, forward rotation and shift-kind fairness are not reached by the
/// engine, and these pins exist so that state cannot quietly get worse while the engine is rebuilt.
/// <para>
/// Since the SpecFirstRed cleanup of 2026-08-12 (SPEC.md decision 11) these pins carry the whole
/// regression protection for the four measurements A4, A5, A7 and A8 used to state — those spec-first
/// assertions were removed because a red test guards nothing. Two things they checked are NOT covered
/// here and are only documented as targets in SPEC.md: the package-length MODE (five days must be the
/// single most frequent length) and the rank-scoped shift-kind spread of ranks 1 to 4, since
/// <see cref="AutofillBaselineTestBase.Baseline_ShiftKindSpreadDidNotGrow"/> measures all employees.
/// </para>
/// <para>
/// Each constant is a band edge, not a single measurement — the worst of what the engine produced
/// under the seeds of <see cref="AutofillSeedBand.Seeds"/>. Re-pinned 2026-08-12 evening by owner
/// decision 13 (SPEC.md) onto the band measured on engine af5f0fa PLUS the decision-12 changes
/// (hour-based unescalatable package rest, fairness trade): the accepted price of the rest guarantee
/// is a splintered package structure, so the package edges moved the wrong way deliberately, while
/// rotation, spread and purity edges tightened. The morning-of-2026-08-12 edges are quoted on each
/// constant.
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
    /// Band over seeds 42/43/44, measured 2026-08-12 evening on the decision-12 engine:
    /// 0.3462/0.3846/0.3462, so the floor is 0.3462. Lowered from 0.4348 (decision 13 — partly a
    /// measurement artifact: more small packages mean more transitions across sufficient free time,
    /// which decision 12b no longer counts as rotation-bound; the A7/A13 measurement rework is
    /// pending). Spec target of the former A7 is 0.80 and stays documented in SPEC.md.
    /// </summary>
    private const double ForwardRate = 0.34615384615384615;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-12 evening on the decision-12 engine: 16/15/16, so
    /// the ceiling is 16. Tightened from 17. Spec target of the former A4 is 0 and stays documented
    /// in SPEC.md.
    /// </summary>
    private const int MixedTypeCount = 16;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-12 evening on the decision-12 engine:
    /// 0.4194/0.4516/0.4194, so the ceiling is 0.4516. Raised from 0.3667 (decision 13 — the accepted
    /// splintering price of the unescalatable rest; the package-aware repair stage is commissioned to
    /// win this back). Spec target of the former A5 is a share of at most 0.20 and stays documented
    /// in SPEC.md.
    /// </summary>
    private const double ShortPackageShare = 0.45161290322580644;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-12 on engine af5f0fa: 0/0/0 — unchanged since
    /// 2026-08-08. Removing the overlong packages is what the operator rework was for, so this pin
    /// stays at zero and is meant to be sharp rather than tolerant.
    /// </summary>
    private const int PackagesOverIdealLength = 0;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-12 evening on the decision-12 engine: 0.1935 on all
    /// three seeds, so the floor is 0.1935. Lowered from 0.2 (decision 13, splintering price).
    /// </summary>
    private const double IdealShare = 0.1935483870967742;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-12 evening on the decision-12 engine: 6/6/6, so the
    /// ceiling is 6. Tightened from 8 — one of the fairness gains of the unescalatable rest. Spec
    /// target of the former A8 is a spread of at most 2 over ranks 1 to 4; that rank-scoped reading
    /// has no pin here, because this guard covers every rank.
    /// </summary>
    private const int ShiftKindSpread = 6;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-12 on engine af5f0fa: 0/0/0, so the ceiling is 0 and
    /// the pin is sharp. Tightened from 1 (band of 2026-08-08, where seed 42 let rank 4 reach a higher
    /// fulfilment than rank 3). CAVEAT before treating a red here as a regression: of all eight pins
    /// this is the one the seed decides most readily — the 2026-08-08 measurement of variant 1b found
    /// 1/1/0 over the same three seeds, and scenario 2 still carries a ceiling of 1 on the same engine.
    /// A single band of three seeds measuring 0/0/0 is therefore weaker evidence of stability here than
    /// it is for the other seven. It is pinned sharply anyway, because A6 no longer asserts the strict
    /// order at all (it judges with a tolerance band since 2026-08-12) and this guard is the only thing
    /// left that does — but a red is a reason to re-measure the band first, not to widen the pin.
    /// </summary>
    private const int MonotonicityViolations = 0;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-12 evening on the decision-12 engine: 664/664/664 h,
    /// so the floor is 664 h. Lowered from 672 h (decision 13 — one eight-hour shift moved from rank 4
    /// to rank 5 under the unescalatable rest; the asserted run gives ranks 1 to 4 184/168/160/152 h
    /// while rank 5 holds 80 h). Rule 5 ranks the top-down service of the guaranteed hours above
    /// rule 6, package integrity, so hours moving down the list is a regression at a high priority
    /// even when nothing else gets worse.
    /// </summary>
    private const double TopRanksPlannedHours = 664;

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
