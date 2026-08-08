// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Fixtures;

/// <summary>
/// The three daily shift kinds of the autofill test suite. The numeric values deliberately match
/// <c>ShiftTypeInference.EarlyIndex/LateIndex/NightIndex</c> so a kind can be compared with a
/// <c>CoreToken.ShiftTypeIndex</c> without a translation table.
/// </summary>
public enum AutofillShiftKind
{
    Early = 0,

    Late = 1,

    Night = 2,
}
