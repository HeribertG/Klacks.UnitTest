// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>Rule 5: rotation direction early to late to night to early across package borders.</summary>
/// <param name="Transitions">
/// Every rotation-bound package-to-package change of every employee. Since the owner ruling
/// 2026-08-12 (SPEC.md decision 12b) a pair separated by at least the configured rest days times
/// 24 hours is a block restart, owes no rotation and does not appear here; only
/// <paramref name="RestSeparatedCount"/> counts it.
/// </param>
/// <param name="ForwardRate">Share of transitions that follow the rotation direction; 0 when there is none</param>
/// <param name="BackwardOrSkipCount">Transitions that go backwards, skip a kind or repeat the same kind</param>
/// <param name="UnexplainedDeviations">
/// Deviating transitions whose reason is unexplained — deviations minus those the ban list provably
/// forced. Without an eligibility input nothing is provable, so this equals BackwardOrSkipCount there
/// </param>
/// <param name="RestSeparatedCount">Package pairs separated by enough rest to owe no rotation</param>
public sealed record RotationMetrics(
    IReadOnlyList<RotationTransition> Transitions,
    double ForwardRate,
    int BackwardOrSkipCount,
    int UnexplainedDeviations,
    int RestSeparatedCount)
{
    /// <summary>
    /// What the shift class did across a long absence, per absent employee and window. A MEASUREMENT:
    /// no documented rotation reset exists in the engine and no owner decision names one, so the
    /// entries are reported and never asserted into a direction. Empty without absences.
    /// </summary>
    public IReadOnlyList<ContinuityAcrossAbsence> ContinuityAcrossAbsence { get; init; } = [];

    /// <summary>
    /// Rotation as SPEC.md decision 12b states it: inside a block the kind never falls. This is the
    /// reading the baseline guard pins. Empty means "no pair", which holds no violation.
    /// </summary>
    public BlockRotationCompliance BlockCompliance { get; init; } = new(0, 0, 1);

    /// <summary>
    /// Naive cyclic reading over every consecutive package pair, comparing the START kind of each
    /// package — exactly the comparison the engine's block-ordering fitness term makes. Reported only.
    /// </summary>
    public CyclicRotationCounts CyclicStartToStart { get; init; } = new(0, 0, 0, 0, 0);

    /// <summary>
    /// Naive cyclic reading over every consecutive package pair, comparing the LAST kind of a package with
    /// the first kind of the next one — the transition a reader of the plan actually sees. Reported only.
    /// </summary>
    public CyclicRotationCounts CyclicLastToFirst { get; init; } = new(0, 0, 0, 0, 0);
}
