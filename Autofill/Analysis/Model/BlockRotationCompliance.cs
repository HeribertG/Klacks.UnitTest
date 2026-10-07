// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// Rotation as owner ruling 2026-08-12 (SPEC.md decision 12b) defines it: a block is a run of shifts
/// without enough rest between them (less than the configured rest days times 24 hours from one shift's
/// end to the next shift's start), and inside a block the shift kind may stay or rise
/// (early, late, night) but never fall. Across enough rest a new block starts freely, so those pairs
/// owe nothing and are not counted. Unlike the package-to-package <see cref="RotationMetrics.ForwardRate"/>
/// this reading also judges kind changes INSIDE a calendar package and treats a night followed by an
/// early shift inside one block as the violation it is under 12b.
/// </summary>
/// <param name="PairCount">Consecutive shift pairs of one employee that lie inside one block</param>
/// <param name="DescendingCount">Pairs among them whose kind falls (late or night to early, night to late)</param>
/// <param name="CompliantRate">
/// Share of the pairs that do not fall; 1 when there is no pair, because an empty block set holds no
/// violation
/// </param>
public sealed record BlockRotationCompliance(
    int PairCount,
    int DescendingCount,
    double CompliantRate);
