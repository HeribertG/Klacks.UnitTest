// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// What the rotation did across a long absence: the shift class of the last package before the window
/// and of the first package after it.
/// <para>
/// This is a MEASUREMENT and not a rule. The discovery of 2026-08-14 found no documented reset of the
/// rotation state after an absence anywhere in the engine, and no owner decision names one, so neither
/// "continues forwards" nor "restarts on early" can be asserted without inventing a specification. The
/// entry states which of the two happened and lets the report judge it.
/// </para>
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="WindowFrom">First day of the absence window</param>
/// <param name="WindowUntil">Last day of the absence window</param>
/// <param name="KindBefore">Shift class of the last package before the window; null when there is none</param>
/// <param name="KindAfter">Shift class of the first package after the window; null when there is none</param>
/// <param name="GapDays">Calendar days between the two packages, the absence days included</param>
/// <param name="ContinuesForward">True when the class after the window is the forward successor of the one before</param>
/// <param name="RestartsOnEarly">True when the class after the window is early while the one before was not late</param>
public sealed record ContinuityAcrossAbsence(
    string Employee,
    DateOnly WindowFrom,
    DateOnly WindowUntil,
    AutofillShiftKind? KindBefore,
    AutofillShiftKind? KindAfter,
    int GapDays,
    bool ContinuesForward,
    bool RestartsOnEarly);
