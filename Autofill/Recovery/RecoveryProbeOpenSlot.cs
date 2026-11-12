// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Autofill.Recovery;

/// <summary>
/// MEASUREMENT, not production code: one missing head of a slot of a finished autofill plan, with the times the recovery
/// probe hands to the engine.
/// </summary>
/// <param name="ShiftRefId">Shift definition of the slot</param>
/// <param name="Date">Day of the slot</param>
/// <param name="Name">Display name of the shift</param>
/// <param name="StartAt">Start instant of the slot</param>
/// <param name="EndAt">End instant of the slot, on the next day for a shift over midnight</param>
/// <param name="Hours">Paid hours of the slot</param>
/// <param name="ShiftTypeIndex">Shift kind as the autofill infers it (0 early, 1 late, 2 night)</param>
public sealed record RecoveryProbeOpenSlot(
    Guid ShiftRefId,
    DateOnly Date,
    string Name,
    DateTime StartAt,
    DateTime EndAt,
    decimal Hours,
    int ShiftTypeIndex);
