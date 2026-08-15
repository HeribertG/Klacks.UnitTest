// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// How one employee's hour target stands once the absence credit is counted — the metric behind the
/// stop-gate answer of 2026-08-14.
/// <para>
/// The target is NOT reduced pro rata. <c>TokenFitnessEvaluator.cs:222-225</c> computes
/// <c>covered = CurrentHours + tokenHours + breakHours</c> and compares that against the unchanged
/// <c>GuaranteedHours</c>, so absence hours arrive as a CREDIT on the actual side. That is why this
/// record carries the original target and never a shortened one.
/// </para>
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="ListRank">Position in the displayed list, 1 = top</param>
/// <param name="OriginalTarget">Contractual target for the period, unchanged by any absence</param>
/// <param name="CreditHours">Hours the absence rows credit; rows without hours contribute nothing</param>
/// <param name="PlannedHours">Hours the plan assigns inside the period</param>
/// <param name="CurrentHours">Hours the employee brought into the period; the third summand of the engine's formula</param>
/// <param name="CoveredIncludingCredit">Current plus planned plus credit — the engine's own coverage sum</param>
/// <param name="Fulfilled">True when the coverage sum reaches the original target</param>
/// <param name="ForcedShortfallDeclared">
/// True when the plan states a shortfall as forced with a cause instead of silently missing the
/// target. A shortfall that is neither fulfilled nor declared is the failure case
/// </param>
/// <param name="ShortfallCause">Cause of a declared shortfall; empty when there is none</param>
public sealed record CreditTargetEntry(
    string Employee,
    int ListRank,
    double OriginalTarget,
    double CreditHours,
    double PlannedHours,
    double CurrentHours,
    double CoveredIncludingCredit,
    bool Fulfilled,
    bool ForcedShortfallDeclared,
    string ShortfallCause)
{
    /// <summary>Hours still missing towards the original target; 0 once it is reached.</summary>
    public double Shortfall => Math.Max(0, OriginalTarget - CoveredIncludingCredit);
}
