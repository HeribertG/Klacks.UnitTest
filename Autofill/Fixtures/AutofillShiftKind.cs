// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Fixtures;

/// <summary>
/// The daily shift kinds of the autofill test suite. The first three numeric values deliberately match
/// <c>ShiftTypeInference.EarlyIndex/LateIndex/NightIndex</c>, but the value is NO LONGER the engine's
/// shift type index — use <see cref="AutofillShiftCatalog.ShiftTypeIndexOf"/> for that and
/// <see cref="AutofillShiftCatalog.SlotIndexOf"/> to address the pinned id table.
/// <para>
/// <see cref="Day"/> is the fourth slot kind scenario 5 introduces, an 08:00-16:00 shift of its own
/// shift reference id per order. A fourth shift CLASS does not exist in the engine: its time span
/// classifies as late, so the day shift is a late shift to every rule about shift kinds and is only
/// distinguishable through its shift reference id. That is the owner decision of 2026-08-14 and the
/// reason the two lookups above are separate.
/// </para>
/// </summary>
public enum AutofillShiftKind
{
    Early = 0,

    Late = 1,

    Night = 2,

    /// <summary>Day shift 08:00-16:00; a slot kind of its own, engine shift class late.</summary>
    Day = 3,
}
