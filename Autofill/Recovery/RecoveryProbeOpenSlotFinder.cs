// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using Klacks.ScheduleOptimizer.Constraints;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;

namespace Klacks.UnitTest.Autofill.Recovery;

/// <summary>
/// MEASUREMENT, not production code: the missing heads of a finished plan, counted exactly like the autofill's own
/// measurement does it: slot capacity from <see cref="EvaluationContext.SlotCapacities"/> minus every token of the plan
/// on that shift and day (locked tokens and stand-in tokens included), one entry per missing head, in date order.
/// </summary>
public static class RecoveryProbeOpenSlotFinder
{
    /// <summary>The open heads of the plan.</summary>
    /// <param name="tokens">Tokens of the finished plan</param>
    /// <param name="context">Context the plan was produced from</param>
    /// <param name="problems">Receives one text per slot whose times cannot be read</param>
    public static List<RecoveryProbeOpenSlot> Find(
        IReadOnlyList<CoreToken> tokens, CoreWizardContext context, List<string> problems)
    {
        var evaluation = EvaluationContext.For(context);
        var assigned = new Dictionary<(Guid, DateOnly), int>();
        foreach (var token in tokens)
        {
            var key = (token.ShiftRefId, token.Date);
            assigned[key] = assigned.GetValueOrDefault(key) + 1;
        }

        var open = new List<RecoveryProbeOpenSlot>();
        foreach (var entry in evaluation.SlotsByKey.OrderBy(e => e.Key.Date).ThenBy(e => e.Key.ShiftRefId))
        {
            var capacity = evaluation.SlotCapacities.GetValueOrDefault(
                entry.Key, Math.Max(1, entry.Value.RequiredAssignments));
            var held = assigned.GetValueOrDefault(entry.Key);
            if (held >= capacity)
            {
                continue;
            }

            var slot = SlotOf(entry.Key.ShiftRefId, entry.Key.Date, entry.Value);
            if (slot is null)
            {
                problems.Add($"slot {entry.Key.ShiftRefId} on {entry.Key.Date.ToString(RecoveryProbeFormats.Date, CultureInfo.InvariantCulture)} has unreadable times '{entry.Value.StartTime}'-'{entry.Value.EndTime}'");
                continue;
            }

            for (; held < capacity; held++)
            {
                open.Add(slot);
            }
        }

        return open;
    }

    /// <summary>The slot of a shift on a day with its real interval, or null when its times cannot be read.</summary>
    /// <param name="shiftRefId">Shift definition</param>
    /// <param name="date">Day of the slot</param>
    /// <param name="shift">Slot as the context carries it</param>
    public static RecoveryProbeOpenSlot? SlotOf(Guid shiftRefId, DateOnly date, CoreShift shift)
    {
        if (!TimeOnly.TryParse(shift.StartTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !TimeOnly.TryParse(shift.EndTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
        {
            return null;
        }

        var startAt = date.ToDateTime(start);
        var endAt = end <= start ? date.AddDays(1).ToDateTime(end) : date.ToDateTime(end);
        return new RecoveryProbeOpenSlot(
            shiftRefId,
            date,
            shift.Name,
            startAt,
            endAt,
            (decimal)shift.Hours,
            ShiftTypeInference.FromSpan(start, end));
    }
}
