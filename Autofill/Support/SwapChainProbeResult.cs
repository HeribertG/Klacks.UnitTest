// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.Autofill.Support;

/// <summary>
/// What the swap-chain measurement found for one rule setting.
/// </summary>
/// <param name="Variant">Rule setting that was measured</param>
/// <param name="OpenBefore">Missing assignments of the finished plan</param>
/// <param name="ClosedByMoves">Closed assignments by chain length; index 0 is one move, a direct fill</param>
/// <param name="StillOpen">Missing assignments no chain was found for</param>
/// <param name="BudgetExhausted">Of the still open ones: searches that ran out of the probe budget</param>
/// <param name="Probes">Rule checks spent</param>
/// <param name="LegalityBefore">Hard violations other than open slots before the chains</param>
/// <param name="LegalityAfter">Hard violations other than open slots after the chains</param>
/// <param name="Chains">Every chain that was applied, as text</param>
/// <param name="Plan">The plan after the chains</param>
/// <param name="PackageRestExceptions">
/// Per applied chain, in the order of <paramref name="Chains"/>: shifts of the chain's employees that keep less than
/// the package rest after the chain and did not before; always zero for a variant that does not shorten it
/// </param>
/// <param name="ToleratedDeviations">Assignments of the baseline plan whose soft deviation the measurement tolerated</param>
public sealed record SwapChainProbeResult(
    SwapChainProbeVariant Variant,
    int OpenBefore,
    IReadOnlyList<int> ClosedByMoves,
    int StillOpen,
    int BudgetExhausted,
    long Probes,
    int LegalityBefore,
    int LegalityAfter,
    IReadOnlyList<string> Chains,
    CoreScenario Plan,
    IReadOnlyList<int> PackageRestExceptions,
    int ToleratedDeviations);
