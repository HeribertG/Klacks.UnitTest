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
/// under the seeds of <see cref="AutofillSeedBand.Seeds"/>. Re-measured and sharpened 2026-08-12 on
/// engine Klacks.ScheduleOptimizer af5f0fa (unchanged since 2026-08-10) from the band artifact of the
/// full suite run of that day; the previous edges dated from 2026-08-08 and were looser than anything
/// the engine still produces. The band artifact
/// <c>artifacts/scenario1b/Scenario1bCalibration.band.json</c> is written on every run and reports the
/// values to copy under "Worst".
/// </para>
/// </summary>
public static class Scenario1bBaselineValues
{
    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-12 on engine af5f0fa: 0.4/0.36/0.4, so the floor is
    /// 0.36 and seed 43 sets it. The asserted run on seed 42 runs 10 of 25 package transitions forward.
    /// Tightened from 0.32 (band of 2026-08-08). Spec target of the former A7 is 0.80 and stays
    /// documented in SPEC.md.
    /// </summary>
    private const double ForwardRate = 0.36;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-12 on engine af5f0fa: 16/16/16, so the ceiling is 16.
    /// The asserted run mixes shift kinds in 16 of 30 packages. Tightened from 18 (band of 2026-08-08).
    /// Spec target of the former A4 is 0 and stays documented in SPEC.md.
    /// </summary>
    private const int MixedTypeCount = 16;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-12 on engine af5f0fa: 0.3667 on all three seeds, so
    /// the ceiling is 0.3667. The asserted run has 11 of 30 packages of at most two days. Tightened
    /// from 0.4 (band of 2026-08-08). Spec target of the former A5 is a share of at most 0.20 and stays
    /// documented in SPEC.md.
    /// </summary>
    private const double ShortPackageShare = 0.36666666666666664;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-12 on engine af5f0fa: 0/0/0 — unchanged since
    /// 2026-08-08. Removing the overlong packages is what the operator rework was for, so this pin
    /// stays at zero and is meant to be sharp rather than tolerant.
    /// </summary>
    private const int PackagesOverIdealLength = 0;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-12 on engine af5f0fa: 0.1/0.1333/0.1, so the floor is
    /// 0.1 and seeds 42 and 44 set it. The asserted run has 3 of 30 packages of five days followed by
    /// exactly two free days. Tightened from 0.0667 (band of 2026-08-08).
    /// </summary>
    private const double IdealShare = 0.1;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-12 on engine af5f0fa: 6/6/6, so the ceiling is 6.
    /// The asserted run spreads early 5, late 3, night 6 over all five employees. Tightened from 10
    /// (band of 2026-08-08). Spec target of the former A8 is a spread of at most 2 over ranks 1 to 5;
    /// that rank-scoped reading has no pin here, because this guard covers every rank.
    /// </summary>
    private const int ShiftKindSpread = 6;

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
