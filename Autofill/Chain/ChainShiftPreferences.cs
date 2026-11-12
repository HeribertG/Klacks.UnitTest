// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.ScheduleOptimizer.Pipeline;

namespace Klacks.UnitTest.Autofill.Chain;

/// <summary>
/// Turns the shift preferences of a scenario context into what Wizard 2/3 read per agent, the way
/// HarmonizerContextBuilder does in production: a Preferred entry becomes the shift symbol of the shift's time span
/// (the same coarse early/late/night/other symbol the bitmap cells carry, so a day shift 08:00-16:00 counts as early),
/// a Blacklist entry becomes a blacklisted shift id.
/// </summary>
public static class ChainShiftPreferences
{
    private const string TimeFormat = "HH:mm";

    /// <summary>Preferred shift symbols per agent; agents without a Preferred entry are absent.</summary>
    /// <param name="context">Scenario context whose preferences are read</param>
    public static IReadOnlyDictionary<string, IReadOnlySet<CellSymbol>> PreferredSymbols(CoreWizardContext context)
    {
        var shifts = ShiftsById(context);
        var result = new Dictionary<string, HashSet<CellSymbol>>(StringComparer.Ordinal);
        foreach (var preference in context.ShiftPreferences.Where(p => p.Kind == ShiftPreferenceKind.Preferred))
        {
            if (!shifts.TryGetValue(preference.ShiftRefId.ToString(), out var shift))
            {
                throw new InvalidOperationException(
                    $"The preference of {preference.AgentId} names shift {preference.ShiftRefId}, which the context does not demand.");
            }

            if (!result.TryGetValue(preference.AgentId, out var symbols))
            {
                symbols = [];
                result[preference.AgentId] = symbols;
            }

            symbols.Add(ChainStage.SymbolOf(TimeOf(shift.StartTime), TimeOf(shift.EndTime)));
        }

        return result.ToDictionary(entry => entry.Key, entry => (IReadOnlySet<CellSymbol>)entry.Value, StringComparer.Ordinal);
    }

    /// <summary>Blacklisted shift ids per agent; agents without a Blacklist entry are absent.</summary>
    /// <param name="context">Scenario context whose preferences are read</param>
    public static IReadOnlyDictionary<string, IReadOnlySet<Guid>> BlacklistedShifts(CoreWizardContext context)
        => context.ShiftPreferences
            .Where(p => p.Kind == ShiftPreferenceKind.Blacklist)
            .GroupBy(p => p.AgentId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlySet<Guid>)group.Select(p => p.ShiftRefId).ToHashSet(),
                StringComparer.Ordinal);

    private static Dictionary<string, CoreShift> ShiftsById(CoreWizardContext context)
        => context.Shifts
            .GroupBy(shift => shift.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

    private static DateTime TimeOf(string time)
        => DateTime.MinValue.Add(TimeOnly.ParseExact(time, TimeFormat, CultureInfo.InvariantCulture).ToTimeSpan());
}
