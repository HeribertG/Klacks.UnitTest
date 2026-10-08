// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// One block change that did not go to the ideal kind of SPEC-ROTATION-2026-10-08.
/// </summary>
/// <param name="Employee">Employee making the change</param>
/// <param name="Date">First day of the new block</param>
/// <param name="From">Last shift class of the previous block</param>
/// <param name="To">First shift class of the new block</param>
/// <param name="Ideal">Shift class the spec would have chosen</param>
/// <param name="Forced">True when a hard rule or full coverage made the ideal kind impossible on that day</param>
/// <param name="Cause">hardRule, coverage or unforced</param>
public sealed record RotationSpecDeviation(
    string Employee,
    DateOnly Date,
    AutofillShiftKind From,
    AutofillShiftKind To,
    AutofillShiftKind Ideal,
    bool Forced,
    string Cause);
