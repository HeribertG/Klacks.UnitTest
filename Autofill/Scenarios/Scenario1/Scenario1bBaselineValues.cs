// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

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
    /// Rotation pin under SPEC.md decision 12b, measured on the shift sequence (in-block pairs: less than
    /// MinRestDays x 24 h between two shifts; the kind must not fall). Band over seeds 42/43/44 measured
    /// 2026-10-07 on Api 6f90aa144 / Optimizer 8035a10: 1/1/1, so the floor is 1 and the pin is sharp — a
    /// single falling in-block pair turns it red. Replaces the vacuous package-pair ForwardRate pin of 0
    /// (F1 of tests/autofill/STOPPGATE-2026-10-07.md). The naive cyclic forward rate over all package
    /// pairs is reported in the band artifact (0.3/0.4167/0.381) but not pinned: which rotation definition binds is
    /// an open owner decision.
    /// </summary>
    private const double BlockRotationCompliance = 1;

    /// <summary>
    /// Floor of the in-block pairs the rotation pin is measured on (rotation.blockCompliance.pairCount). Band over seeds
    /// 42/43/44 measured 2026-10-08 on Api 104c0b418 / Optimizer 43529c2 plus the SlotConstraintFilter refactor (plans
    /// byte-identical): 68/64/67, so the floor is 64. Without it the rotation pin of 1 would also pass a plan with no
    /// in-block pair at all, because the rate is 1 when there is nothing to measure.
    /// 2026-10-10: lowered 64 to 62 by owner decision; the rotation round 4 (SPEC-ROTATION-2026-10-08 end passes)
    /// leaves 62 in-block pairs on the asserted seed.
    /// </summary>
    private const int BlockRotationPairCount = 62;

    /// <summary>
    /// Band over seeds 42/43/44 after the M11 fairness stage of 2026-08-13: 3/18/14, so the
    /// ceiling is 18. TIGHTENED from 24; the asserted seed holds only 3 mixed packages. Spec
    /// target of the former A4 is 0 and stays documented in SPEC.md.
    /// </summary>
    private const int MixedTypeCount = 18;

    /// <summary>
    /// Band over seeds 42/43/44 after the M11 fairness stage of 2026-08-13: 0.12/0.2258/0.30, so
    /// the ceiling is 0.30 (widened by seed 44 alone; the asserted seed reaches 0.12 — far under
    /// the 0.20 spec target of the former A5, which stays documented in SPEC.md).
    /// </summary>
    private const double ShortPackageShare = 0.3;

    /// <summary>
    /// Band over seeds 42/43/44, measured 2026-08-12 on engine af5f0fa: 0/0/0 — unchanged since
    /// 2026-08-08. Removing the overlong packages is what the operator rework was for, so this pin
    /// stays at zero and is meant to be sharp rather than tolerant.
    /// </summary>
    private const int PackagesOverIdealLength = 0;

    /// <summary>
    /// Band over seeds 42/43/44 after the M11 fairness stage of 2026-08-13: 0.16/0.0323/0.0333, so
    /// the floor is 0.0323. TIGHTENED from 0 — the first non-zero ideal floor of this variant
    /// since decision 13 called its zero "the harshest splintering price in the suite"; the
    /// asserted seed reaches 0.16.
    /// 2026-10-10: lowered to 0 by owner decision; after the rotation round 4 (rotation outranks purity,
    /// SPEC-ROTATION-2026-10-08) the asserted seed holds 0 of 31 five-two packages. A floor of 0 cannot fail.
    /// </summary>
    private const double IdealShare = 0;

    /// <summary>
    /// Band over seeds 42/43/44 after the M11 fairness stage of 2026-08-13: 8/9/6, so the ceiling
    /// is 9 (owner decision of the same evening — part of the stage's accepted fairness price).
    /// Spec target of the former A8 is a spread of at most 2 over ranks 1 to 5; that rank-scoped
    /// reading has no pin here, because this guard covers every rank.
    /// 2026-09-30: re-pinned 9 to 14 after weekly rest-day enforcement removed real violations (owner-approved).
    /// </summary>
    private const int ShiftKindSpread = 14;

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
        MinBlockRotationCompliance: BlockRotationCompliance,
        MinBlockRotationPairCount: BlockRotationPairCount,
        MaxMixedTypeCount: MixedTypeCount,
        MaxShortPackageShare: ShortPackageShare,
        MaxPackagesOverIdealLength: PackagesOverIdealLength,
        MinIdealShare: IdealShare,
        MaxShiftKindSpread: ShiftKindSpread,
        MaxMonotonicityViolations: MonotonicityViolations,
        MinTopRanksPlannedHours: TopRanksPlannedHours);
}
