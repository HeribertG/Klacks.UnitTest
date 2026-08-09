// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>Rule 2: physical and legal admissibility of the plan.</summary>
/// <param name="RestViolations">Shift pairs below the contractual rest time, carry-in days included</param>
/// <param name="NightToEarlyViolations">Night shift directly followed by an early shift</param>
public sealed record LegalityMetrics(
    IReadOnlyList<RestTimeViolation> RestViolations,
    IReadOnlyList<NightToEarlyViolation> NightToEarlyViolations)
{
    /// <summary>
    /// The subset of <see cref="RestViolations"/> whose two shifts belong to different orders — the
    /// proof that the rest check keeps seeing the whole employee once the day spreads over parallel
    /// orders. Empty without an order dimension.
    /// </summary>
    public IReadOnlyList<CrossOrderRestViolation> RestViolationsCrossOrder { get; init; } = [];
}
