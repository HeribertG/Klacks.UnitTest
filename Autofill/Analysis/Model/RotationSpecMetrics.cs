// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// Rotation as the owner defined it on 2026-10-08 (tests/autofill/SPEC-ROTATION-2026-10-08.md), measured on the plan
/// by the test oracle <c>RotationSpecAnalyzer</c>, independently of the engines. A block is a run of shifts whose
/// rest to each other is below 48 hours; inside a block the kind should stay the same, between blocks the ideal is
/// early to late to night to early, a kind the employee may not work on the new block's days is skipped, and after a
/// pause of at least 7 calendar days the cycle restarts at early. Carry-in shifts take part as predecessors.
/// </summary>
/// <param name="BlockCount">Blocks with at least one shift inside the period</param>
/// <param name="PureBlockCount">Of those, blocks without a change of kind</param>
/// <param name="BlockPurity">PureBlockCount divided by BlockCount; 1 when there is no block (rotation.blockPurity)</param>
/// <param name="TransitionCount">Block changes into a block that starts inside the period and owes a rotation</param>
/// <param name="IdealTransitionCount">Of those, changes that went to the ideal kind</param>
/// <param name="IdealTransitionRate">IdealTransitionCount divided by TransitionCount; 1 when there is none (rotation.idealTransitionRate)</param>
/// <param name="ForcedDeviationCount">Non-ideal changes the plan provably had to make (hard rule or full coverage of the ideal kind)</param>
/// <param name="UnforcedDeviations">Non-ideal changes without such proof; target 0 (rotation.unforcedDeviations)</param>
/// <param name="SingleKindSkipCount">Block changes not counted because at most one kind was allowed (rotation does not apply)</param>
/// <param name="LongPauseRestartCount">Block changes after a long pause, where the ideal kind is the first allowed one from early</param>
/// <param name="Deviations">Every non-ideal change, forced or not, for diagnosis</param>
public sealed record RotationSpecMetrics(
    int BlockCount,
    int PureBlockCount,
    double BlockPurity,
    int TransitionCount,
    int IdealTransitionCount,
    double IdealTransitionRate,
    int ForcedDeviationCount,
    int UnforcedDeviations,
    int SingleKindSkipCount,
    int LongPauseRestartCount,
    IReadOnlyList<RotationSpecDeviation> Deviations)
{
    public static RotationSpecMetrics Empty { get; } = new(0, 0, 1, 0, 0, 1, 0, 0, 0, 0, []);
}
