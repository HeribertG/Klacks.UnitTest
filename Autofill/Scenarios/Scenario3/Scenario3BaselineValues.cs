// Copyright (c) Heribert Gasparoli Private. All rights reserved.

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
    /// Measured on L1, seed 42, under the decision-12b measurement rework of 2026-08-13: all 27
    /// package pairs are free restarts across enough rest, the rotation-bound set is empty and the
    /// rate has no subject (the guard holds vacuously). The old floor 0.5185 measured free restarts.
    /// Spec target of the former A7 is 0.80 (SPEC.md).
    /// </summary>
    private const double ForwardRate = 0;

    /// <summary>
    /// Measured on L1, seed 42, 2026-08-12 evening on the decision-12 engine: 8 of 32 packages mix
    /// shift kinds. Tightened from 9. Spec target of the former A4 is 0 (SPEC.md).
    /// </summary>
    private const int MixedTypeCount = 8;

    /// <summary>
    /// Measured on L1, seed 42, 2026-08-12 evening on the decision-12 engine: 11 of 32 packages are at
    /// most two days long. Tightened from 0.4063 — scenario 3 is the one scenario whose short-package
    /// share IMPROVED under the rest guarantee. Spec target of the former A5 is a share of at most
    /// 0.20 (SPEC.md).
    /// </summary>
    private const double ShortPackageShare = 0.34375;

    /// <summary>
    /// Measured on L1, seed 42, 2026-08-12, engine af5f0fa: no package exceeds the five-day ideal, the
    /// February part of a continued package included. Sharp at zero like in every other scenario.
    /// </summary>
    private const int PackagesOverIdealLength = 0;

    /// <summary>
    /// Measured on L1, seed 42, 2026-08-12 evening on the decision-12 engine: 4 of 32 packages are
    /// five days followed by exactly two free days. Lowered from 0.1875 (decision 13, splintering
    /// price of the unescalatable rest).
    /// </summary>
    private const double IdealShare = 0.125;

    /// <summary>
    /// Measured on L1, seed 42, 2026-08-12 evening on the decision-12 engine: early 13, late 8,
    /// night 13 over all five employees, so the widest spread is 13. Tightened from 17 — the night
    /// imbalance the ban list forces (MA-3/MA-4 hold zero nights) shrank with the fairer night
    /// distribution (A25 cohort spread 0.398 to 0.119). Spec target of the former A8 is a spread of
    /// at most 2 over ranks 1 to 4 (SPEC.md); that rank-scoped reading has no pin here.
    /// </summary>
    private const int ShiftKindSpread = 13;

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
        MinForwardRate: ForwardRate,
        MaxMixedTypeCount: MixedTypeCount,
        MaxShortPackageShare: ShortPackageShare,
        MaxPackagesOverIdealLength: PackagesOverIdealLength,
        MinIdealShare: IdealShare,
        MaxShiftKindSpread: ShiftKindSpread,
        MaxMonotonicityViolations: MonotonicityViolations,
        MinTopRanksPlannedHours: TopRanksPlannedHours);
}
