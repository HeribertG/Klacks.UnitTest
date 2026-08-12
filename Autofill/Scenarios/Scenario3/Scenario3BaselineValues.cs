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
/// Klacks.ScheduleOptimizer af5f0fa, full suite run of 2026-08-12, artifact
/// <c>artifacts/scenario3/Scenario3L1.run1.metrics.json</c>, reproduced byte-identically by run 2.
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
    /// Measured on L1, seed 42, 2026-08-12, engine af5f0fa: 7 of 27 package transitions run forward.
    /// The lowest forward rate of the whole suite — the ban list closes the night kind for MA-3 and
    /// MA-4 entirely, so early to late to early is their only lawful rhythm and every one of their
    /// package boundaries counts as a deviation. Spec target of the former A7 is 0.80 (SPEC.md).
    /// </summary>
    private const double ForwardRate = 0.25925925925925924;

    /// <summary>
    /// Measured on L1, seed 42, 2026-08-12, engine af5f0fa: 9 of 32 packages mix shift kinds. Spec
    /// target of the former A4 is 0 (SPEC.md).
    /// </summary>
    private const int MixedTypeCount = 9;

    /// <summary>
    /// Measured on L1, seed 42, 2026-08-12, engine af5f0fa: 13 of 32 packages are at most two days
    /// long. The worst short-package share of the suite; the night pool of the second half is two
    /// employees wide, which forces the cut A19 accounts for. Spec target of the former A5 is a share
    /// of at most 0.20 (limit 0.15 plus tolerance 0.05, SPEC.md).
    /// </summary>
    private const double ShortPackageShare = 0.40625;

    /// <summary>
    /// Measured on L1, seed 42, 2026-08-12, engine af5f0fa: no package exceeds the five-day ideal, the
    /// February part of a continued package included. Sharp at zero like in every other scenario.
    /// </summary>
    private const int PackagesOverIdealLength = 0;

    /// <summary>
    /// Measured on L1, seed 42, 2026-08-12, engine af5f0fa: 6 of 32 packages are five days followed by
    /// exactly two free days.
    /// </summary>
    private const double IdealShare = 0.1875;

    /// <summary>
    /// Measured on L1, seed 42, 2026-08-12, engine af5f0fa: early 10, late 12, night 17 over all five
    /// employees, so the widest spread is 17. It is this large by construction rather than by defect —
    /// MA-3 and MA-4 hold zero nights under the ban list while MA-1 holds 17 — so this pin only guards
    /// against the imbalance growing further. Spec target of the former A8 is a spread of at most 2
    /// over ranks 1 to 4 (SPEC.md); that rank-scoped reading has no pin here.
    /// </summary>
    private const int ShiftKindSpread = 17;

    /// <summary>
    /// Measured on L1, seed 42, 2026-08-12, engine af5f0fa: 2 ranks reach a higher fulfilment than the
    /// rank above them (rank 2 at 97.8 % over rank 1 at 93.3 %, rank 5 at 71.1 % over rank 4 at
    /// 66.7 %). This is the STRICT pairwise count A6 no longer asserts: since 2026-08-12 A6 judges the
    /// order against a tolerance band of one daily working time, so the sharp reading survives only
    /// here. Spec target is 0 (SPEC.md rule 5).
    /// </summary>
    private const int MonotonicityViolations = 2;

    /// <summary>
    /// Measured on L1, seed 42, 2026-08-12, engine af5f0fa: ranks 1 to 4 hold 168/176/152/120 h = 616 h
    /// together, while rank 5 holds 128 h. Rule 5 ranks the top-down service of the guaranteed hours
    /// above rule 6, package integrity, so hours moving down the list is a regression at a high
    /// priority even when nothing else gets worse.
    /// </summary>
    private const double TopRanksPlannedHours = 616;

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
