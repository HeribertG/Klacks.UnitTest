// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>Result of running the same input twice with the same seed.</summary>
/// <param name="RunsIdentical">True when both runs produced the same assignments and the same fitness</param>
/// <param name="FirstDifference">Human-readable description of the first difference found; null when identical</param>
public sealed record DeterminismMetrics(bool RunsIdentical, string? FirstDifference);
