// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario3;

/// <summary>
/// The not-yet-pinned no-regression floor of scenario 3. The scenario was written on 2026-08-08 and
/// has not produced a seed-band measurement yet, so every value below is the mathematically inert
/// bound of its metric — a floor of zero or a ceiling of the metric's maximum. The inherited
/// <c>Baseline_</c> guards therefore run and stay green WITHOUT asserting anything for scenario 3
/// until the owner pins real band values from the first phase-D band measurement; a green
/// <c>Baseline_</c> test of this scenario means "not pinned", never "verified stable".
/// <para>
/// Deliberately no seed-band run is triggered for scenario 3 yet (unlike scenario 1/1b/2): the
/// fixture family already needs eight evolution runs per suite execution (L0, L1, L4, L5, each
/// twice), and the band seeds would add six more before a single pin exists to compare against.
/// Pinning is the same owner-driven re-pin workflow the suite already uses for scenario 1b.
/// </para>
/// </summary>
public static class Scenario3BaselineValues
{
    /// <summary>Inert floor: a rate cannot fall below zero.</summary>
    private const double UnpinnedMinRate = 0;

    /// <summary>Inert floor: a share cannot fall below zero.</summary>
    private const double UnpinnedMinShare = 0;

    /// <summary>Inert ceiling: a share cannot exceed one.</summary>
    private const double UnpinnedMaxShare = 1;

    /// <summary>Inert ceiling: a count cannot exceed the integer range.</summary>
    private const int UnpinnedMaxCount = int.MaxValue;

    /// <summary>Inert floor: an hour sum cannot fall below zero.</summary>
    private const double UnpinnedMinHours = 0;

    /// <summary>The inert placeholder floor of scenario 3; to be replaced by measured band edges.</summary>
    public static AutofillBaseline Baseline { get; } = new(
        MinForwardRate: UnpinnedMinRate,
        MaxMixedTypeCount: UnpinnedMaxCount,
        MaxShortPackageShare: UnpinnedMaxShare,
        MaxPackagesOverIdealLength: UnpinnedMaxCount,
        MinIdealShare: UnpinnedMinShare,
        MaxShiftKindSpread: UnpinnedMaxCount,
        MaxMonotonicityViolations: UnpinnedMaxCount,
        MinTopRanksPlannedHours: UnpinnedMinHours);
}
