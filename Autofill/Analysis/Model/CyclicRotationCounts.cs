// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// The naive cyclic reading of rotation, early to late to night to early, applied to EVERY pair of
/// consecutive packages of one employee regardless of the rest between them. It is a measurement for
/// comparison only — SPEC.md decision 12b makes a pair across enough rest a free restart — and it is the
/// reading the engine's block-ordering fitness term optimises. Each transition lands in exactly one of
/// the three buckets: the cyclic successor, the same kind, or the cyclic predecessor (which is also a
/// skip of one kind forwards, for example early to night).
/// </summary>
/// <param name="PairCount">Consecutive package pairs measured</param>
/// <param name="ForwardCount">Pairs whose next kind is the cyclic successor</param>
/// <param name="SameCount">Pairs that repeat the kind</param>
/// <param name="BackwardCount">Pairs whose next kind is the cyclic predecessor</param>
/// <param name="ForwardRate">ForwardCount divided by PairCount; 0 when there is no pair</param>
public sealed record CyclicRotationCounts(
    int PairCount,
    int ForwardCount,
    int SameCount,
    int BackwardCount,
    double ForwardRate);
