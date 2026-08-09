// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>Rule 5: rotation direction early to late to night to early across package borders.</summary>
/// <param name="Transitions">Every package-to-package change of every employee</param>
/// <param name="ForwardRate">Share of transitions that follow the rotation direction; 0 when there is none</param>
/// <param name="BackwardOrSkipCount">Transitions that go backwards, skip a kind or repeat the same kind</param>
/// <param name="UnexplainedDeviations">
/// Deviating transitions whose reason is unexplained — deviations minus those the ban list provably
/// forced. Without an eligibility input nothing is provable, so this equals BackwardOrSkipCount there
/// </param>
public sealed record RotationMetrics(
    IReadOnlyList<RotationTransition> Transitions,
    double ForwardRate,
    int BackwardOrSkipCount,
    int UnexplainedDeviations);
