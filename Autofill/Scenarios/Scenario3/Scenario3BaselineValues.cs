// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario3;

/// <summary>
/// The no-regression floor of scenario 3, measured on the main run L1. Until 2026-08-12 every value
/// here was the mathematically inert bound of its metric (a floor of zero, a ceiling of
/// <see cref="int.MaxValue"/>), so the inherited <c>Baseline_</c> guards ran green without asserting
/// anything. The SpecFirstRed cleanup of 2026-08-12 (SPEC.md decision 11) removed the permanently red
/// A4, A5, A7 and A8 of this scenario, which made the inert pins the only thing left watching those
/// measurements — nothing. They are therefore replaced by the values the engine actually reaches.
/// <para>
/// UNLIKE scenario 1, 1b and 2 these are NOT band edges. Scenario 3 deliberately measures no seed band
/// (the family already needs eight evolution runs per suite execution; the band seeds would add six
/// more), so every constant below is the single measurement of the asserted run on seed 42 —
/// engine af5f0fa plus the decision-12 changes, re-pinned by owner decision 13 on the verification
/// run of 2026-08-12 evening, artifact <c>artifacts/scenario3/Scenario3L1.run1.metrics.json</c>.
/// That makes these pins sharper than the record <see cref="AutofillBaseline"/> describes: a change
/// that only moves the search trajectory can turn them red without the engine having got worse. When
/// that happens, judge the change instead of widening the pin blindly — or measure a real band first.
/// </para>
/// <para>
/// Two measurements the deleted assertions covered have no pin here either and stay documented as
/// targets in SPEC.md: the package-length MODE (five days must be the single most frequent length) and
/// the rank-scoped shift-kind spread of ranks 1 to 4, since
/// <see cref="AutofillBaselineTestBase.Baseline_ShiftKindSpreadDidNotGrow"/> measures all employees.
/// </para>
/// </summary>
public static class Scenario3BaselineValues
{
    /// <summary>
    /// Rotation pin under SPEC.md decision 12b, measured on the shift sequence (in-block pairs: less than
    /// MinRestDays x 24 h between two shifts; the kind must not fall). Measured 2026-10-07 on L1, seed 42,
    /// Api 6f90aa144 / Optimizer 8035a10: 0 of 70 in-block pairs fall, so the pin is 1 and sharp. Scenario 3
    /// has no seed band, so this is a single-run value. Replaces the vacuous package-pair ForwardRate pin
    /// of 0 (F1 of tests/autofill/STOPPGATE-2026-10-07.md). The naive cyclic forward rate (L1: 9 of 22
    /// package pairs, 0.4091) is reported in the metrics artifact but not pinned.
    /// </summary>
    private const double BlockRotationCompliance = 1;

    /// <summary>
    /// Floor of the in-block pairs the rotation pin is measured on (rotation.blockCompliance.pairCount). Scenario 3 has no
    /// seed band, so this is the single measurement of L1, seed 42, on 2026-10-08 (Api 104c0b418 / Optimizer 43529c2 plus
    /// the SlotConstraintFilter refactor, plans byte-identical): 70 pairs. Sharp like the other scenario 3 pins. Without it
    /// the rotation pin of 1 would also pass a plan with no in-block pair at all.
    /// </summary>
    private const int BlockRotationPairCount = 70;

    /// <summary>
    /// Measured on L1, seed 42, after the M11 fairness stage of 2026-08-13: 5 of 26 packages mix
    /// shift kinds. TIGHTENED from 10. Spec target of the former A4 is 0 (SPEC.md).
    /// </summary>
    private const int MixedTypeCount = 5;

    /// <summary>
    /// Measured on L1, seed 42, after the M11 fairness stage of 2026-08-13: 6 of 26 packages are
    /// at most two days long. TIGHTENED from 0.3333. Spec target of the former A5 is a share of
    /// at most 0.20 (SPEC.md).
    /// </summary>
    private const double ShortPackageShare = 0.23076923076923078;

    /// <summary>
    /// Owner decision 2026-08-13 evening (M11 fairness stage): the pin follows the measured 2 —
    /// both six-day packages are rung-3 coverage escalations, spec-conform "coverage before the
    /// ideal", and every other scenario stays sharp at zero across all band seeds. The former
    /// "meant to stay sharp" reading is deliberately widened HERE ONLY; hunting the six-day
    /// channel stays an open point of the stage report.
    /// </summary>
    private const int PackagesOverIdealLength = 2;

    /// <summary>
    /// Measured on L1, seed 42, after the M11 fairness stage of 2026-08-13: 7 of 26 packages are
    /// five days followed by exactly two free days. TIGHTENED from 0.1515.
    /// </summary>
    private const double IdealShare = 0.26923076923076922;

    /// <summary>
    /// Measured on L1, seed 42, after the M11 fairness stage of 2026-08-13: early 11, late 9,
    /// night 15, so the widest spread is 15 (owner decision of the same evening — the ban list
    /// still forces MA-3/MA-4 to zero nights, so the night row widens while the COHORT fairness
    /// A25 heals to a spread of 0.31). Spec target of the former A8 is a spread of at most 2 over
    /// ranks 1 to 4 (SPEC.md); that rank-scoped reading has no pin here.
    /// 2026-09-30: re-pinned 15 to 17 after weekly rest-day enforcement removed real violations (owner-approved).
    /// </summary>
    private const int ShiftKindSpread = 17;

    /// <summary>
    /// Measured on L1, seed 42, 2026-08-12 evening on the decision-12 engine: 1 rank reaches a higher
    /// fulfilment than the rank above it (rank 5 at 84.4 % over rank 4 at 75.6 %). Tightened from 2.
    /// This is the STRICT pairwise count A6 no longer asserts: since 2026-08-12 A6 judges the order
    /// against a tolerance band, so the sharp reading survives only here. Spec target is 0 (SPEC.md
    /// rule 5).
    /// </summary>
    private const int MonotonicityViolations = 1;

    /// <summary>
    /// Measured on L1, seed 42, 2026-08-12 evening on the decision-12 engine: ranks 1 to 4 hold
    /// 160/152/144/136 h = 592 h together, while rank 5 holds 152 h. Lowered from 616 h (decision 13 —
    /// the unescalatable rest levels the hours across the roster in this scenario; winning the
    /// top-down order back is part of the commissioned package-aware repair stage). Rule 5 ranks the
    /// top-down service of the guaranteed hours above rule 6, so hours moving down the list stays a
    /// regression this floor must catch.
    /// </summary>
    private const double TopRanksPlannedHours = 592;

    /// <summary>The floor scenario 3 must not fall below.</summary>
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
