// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints.Keywords;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.Autofill.Chain;

/// <summary>
/// Turns the schedule commands of a scenario context into the keyword days and day restrictions Wizard 2/3 read, the
/// way HarmonizerKeywordDayResolver does in production: several commands of one (agent, day) are reduced to one
/// <see cref="KeywordDay"/>, a combined FREE closes the day, a combined OnlyX requires and a combined NoX forbids the
/// shift symbol X, a NotFree-only day restricts nothing.
/// </summary>
public static class ChainKeywordDays
{
    /// <summary>The keyword day of every (agent, day) the context's commands govern.</summary>
    /// <param name="context">Scenario context whose commands are reduced</param>
    public static IReadOnlyDictionary<(string AgentId, DateOnly Date), KeywordDay> Of(CoreWizardContext context)
    {
        var days = new Dictionary<(string AgentId, DateOnly Date), KeywordDay>();
        foreach (var day in context.ScheduleCommands.GroupBy(command => (command.AgentId, command.Date)))
        {
            if (KeywordDay.From(day.Select(command => command.Keyword)) is { } keywordDay)
            {
                days[day.Key] = keywordDay;
            }
        }

        return days;
    }

    /// <summary>The closing and symbol restrictions of one keyword day; none for a day without commands.</summary>
    /// <param name="day">Keyword day of the (agent, day), or null when no command governs it</param>
    public static (bool IsFree, CellSymbol? Required, CellSymbol? Forbidden) RestrictionOf(KeywordDay? day)
        => day?.Combined switch
        {
            ScheduleCommandKeyword.Free => (true, null, null),
            ScheduleCommandKeyword.OnlyEarly => (false, CellSymbol.Early, null),
            ScheduleCommandKeyword.OnlyLate => (false, CellSymbol.Late, null),
            ScheduleCommandKeyword.OnlyNight => (false, CellSymbol.Night, null),
            ScheduleCommandKeyword.NoEarly => (false, null, CellSymbol.Early),
            ScheduleCommandKeyword.NoLate => (false, null, CellSymbol.Late),
            ScheduleCommandKeyword.NoNight => (false, null, CellSymbol.Night),
            _ => (false, null, null),
        };
}
