// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;

namespace Klacks.UnitTest.Autofill.Support;

/// <summary>
/// One shift that looks for an employee inside a swap chain: the open slot itself or a shift an
/// employee hands on to take it.
/// </summary>
/// <param name="ShiftRefId">Shift definition</param>
/// <param name="Date">Day the shift starts</param>
/// <param name="ShiftTypeIndex">Shift kind index</param>
/// <param name="Hours">Paid hours</param>
/// <param name="StartAt">Start of the shift</param>
/// <param name="EndAt">End of the shift</param>
/// <param name="Source">Token the shift is carried by today; null for the open slot</param>
public sealed record SwapChainProbeItem(
    Guid ShiftRefId,
    DateOnly Date,
    int ShiftTypeIndex,
    decimal Hours,
    DateTime StartAt,
    DateTime EndAt,
    CoreToken? Source)
{
    /// <summary>Builds the item of an open slot exactly as the coverage repair reads it.</summary>
    /// <param name="shiftRefId">Shift definition</param>
    /// <param name="date">Day of the slot</param>
    /// <param name="slot">Slot definition</param>
    public static SwapChainProbeItem ForOpenSlot(Guid shiftRefId, DateOnly date, CoreShift slot)
    {
        var start = TimeOnly.TryParse(slot.StartTime, out var parsedStart) ? parsedStart : new TimeOnly(8, 0);
        var end = TimeOnly.TryParse(slot.EndTime, out var parsedEnd) ? parsedEnd : start.AddHours(8);
        return new SwapChainProbeItem(
            shiftRefId,
            date,
            ShiftTypeInference.FromSpan(start, end),
            (decimal)slot.Hours,
            date.ToDateTime(start),
            end <= start ? date.AddDays(1).ToDateTime(end) : date.ToDateTime(end),
            null);
    }

    /// <summary>Builds the item of a shift an employee hands on.</summary>
    /// <param name="token">Token that changes its owner</param>
    public static SwapChainProbeItem ForHandedOn(CoreToken token)
        => new(token.ShiftRefId, token.Date, token.ShiftTypeIndex, token.TotalHours, token.StartAt, token.EndAt, token);
}
