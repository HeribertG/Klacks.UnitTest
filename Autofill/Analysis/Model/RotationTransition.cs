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
/// True only when the forward successor was provably unavailable. The one proof a finished plan
/// allows is the fixture ban list: when it closes the forward successor kind for the whole next
/// package, the deviation was forced and <paramref name="Reason"/> says keywordIneligible. Every
/// other cause cannot be reconstructed from the plan and stays false; see the notes on the metrics
/// object.
/// </param>
/// <param name="Reason">
/// One of the words in <see cref="RotationTransitionReason"/>: none for a forward transition,
/// keywordIneligible for a ban-list-forced deviation, unexplained for everything else the analyzer
/// cannot prove.
/// </param>
public sealed record RotationTransition(
    string Employee,
    AutofillShiftKind FromType,
    AutofillShiftKind ToType,
    bool Forward,
    bool Forced,
    string Reason);
