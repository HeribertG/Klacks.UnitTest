// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// The shift-kind change from one package to the next package of the same employee.
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="FromType">Kind of the earlier package</param>
/// <param name="ToType">Kind of the later package</param>
/// <param name="Forward">True for early to late, late to night, night to early</param>
/// <param name="Forced">
/// True only when the forward successor was provably unavailable. The analyzer cannot reconstruct
/// that from the finished plan and always reports false; see the notes on the metrics object.
/// </param>
public sealed record RotationTransition(
    string Employee,
    AutofillShiftKind FromType,
    AutofillShiftKind ToType,
    bool Forward,
    bool Forced);
