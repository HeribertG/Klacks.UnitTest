// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.Autofill.Fixtures;

/// <summary>
/// The booked absences of a scenario, in one object that feeds both sides of a run: the engine gets
/// <c>CoreWizardContext.BreakBlockers</c> from it, and the analyzer reads the same object to decide
/// which day of which employee was absent. One source, so a measurement can never judge against a
/// different absence list than the one the engine planned with.
/// <para>
/// An absence is NOT the FREE keyword and the two must never be modelled through each other. Both
/// block a whole day and both are hard, but they are separate engine fields with separate vetoes
/// (<c>Stage0HardConstraintChecker.cs:164</c> BreakBlocker versus <c>:170</c> KeywordFree), and only
/// the absence carries hours towards the target. A scenario that expressed holidays as FREE would
/// test the exact opposite of what the holiday credit claims.
/// </para>
/// </summary>
public sealed class AutofillBreakBlockerInput
{
    private readonly List<AutofillBreakBlocker> _rows;

    /// <param name="rows">One row per absent employee and calendar day</param>
    public AutofillBreakBlockerInput(IEnumerable<AutofillBreakBlocker> rows)
    {
        _rows = [.. rows];
    }

    /// <summary>The absence of no employee at all — the state every scenario before scenario 6 is in.</summary>
    public static AutofillBreakBlockerInput Empty { get; } = new([]);

    /// <summary>All absence days in declaration order.</summary>
    public IReadOnlyList<AutofillBreakBlocker> Rows => _rows;

    /// <summary>True when the scenario declares no absence at all.</summary>
    public bool IsEmpty => _rows.Count == 0;

    /// <summary>The engine field: one blocker per row, ordered so two runs serialise identically.</summary>
    public IReadOnlyList<CoreBreakBlocker> ToCoreBlockers()
        => _rows
            .OrderBy(r => r.AgentId, StringComparer.Ordinal)
            .ThenBy(r => r.Date)
            .Select(r => r.ToCore())
            .ToList();

    /// <summary>True when that employee is absent on that day.</summary>
    /// <param name="agentId">Employee identifier</param>
    /// <param name="date">Calendar day to test</param>
    public bool Covers(string agentId, DateOnly date)
        => _rows.Any(r => string.Equals(r.AgentId, agentId, StringComparison.Ordinal) && r.Date == date);

    /// <summary>Absence days of one employee, in date order.</summary>
    /// <param name="agentId">Employee identifier</param>
    public IReadOnlyList<DateOnly> DaysOf(string agentId)
        => _rows
            .Where(r => string.Equals(r.AgentId, agentId, StringComparison.Ordinal))
            .Select(r => r.Date)
            .OrderBy(d => d)
            .ToList();

    /// <summary>Employees carrying at least one absence day, in ordinal order.</summary>
    public IReadOnlyList<string> Employees()
        => _rows
            .Select(r => r.AgentId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Hours the absence rows of one employee grant towards the hour target, summed the way
    /// <c>ComputeBreakHoursByAgent</c> sums them: rows without hours contribute nothing, and because
    /// every row spans exactly one day the day factor of that method stays 1.
    /// </summary>
    /// <param name="agentId">Employee identifier</param>
    public decimal CreditHoursOf(string agentId)
        => _rows
            .Where(r => string.Equals(r.AgentId, agentId, StringComparison.Ordinal) && r.GrantsCredit)
            .Sum(r => r.Hours);

    /// <summary>
    /// The maximal runs of consecutive absence days of one employee — the WINDOWS a reader thinks in,
    /// rebuilt from the one-day rows the engine actually gets.
    /// </summary>
    /// <param name="agentId">Employee identifier</param>
    public IReadOnlyList<(DateOnly From, DateOnly Until)> WindowsOf(string agentId)
    {
        var days = DaysOf(agentId);
        var windows = new List<(DateOnly From, DateOnly Until)>();
        var index = 0;
        while (index < days.Count)
        {
            var start = index;
            while (index + 1 < days.Count && days[index + 1] == days[index].AddDays(1))
            {
                index++;
            }

            windows.Add((days[start], days[index]));
            index++;
        }

        return windows;
    }

    /// <summary>Every absence window of every employee, in employee and date order.</summary>
    public IReadOnlyList<(string AgentId, DateOnly From, DateOnly Until)> AllWindows()
        => Employees()
            .SelectMany(agent => WindowsOf(agent).Select(w => (AgentId: agent, w.From, w.Until)))
            .ToList();

    /// <summary>
    /// Checks the input against the scenario it is attached to. Four mistakes would otherwise pass
    /// silently and produce a measurement of nothing or of the wrong thing: naming an employee the
    /// scenario does not have, placing a day outside the planning period — <c>ComputeBreakHoursByAgent</c>
    /// clips such a day away (<c>:267-268</c>) and <c>IsBlockedByBreak</c> would block a day nobody
    /// plans — stacking two rows on one date, which double-credits the day because the method
    /// accumulates per AGENT without a date key (<c>:276-278</c>), and declaring negative hours.
    /// </summary>
    /// <param name="context">Assembled engine context of the scenario</param>
    /// <param name="employees">Employee ids of the scenario</param>
    public IReadOnlyList<string> ValidationProblems(CoreWizardContext context, IReadOnlyList<string> employees)
    {
        var problems = new List<string>();
        var known = employees.ToHashSet(StringComparer.Ordinal);

        foreach (var row in _rows)
        {
            if (!known.Contains(row.AgentId))
            {
                problems.Add($"Absence row names employee '{row.AgentId}', who is not part of the scenario.");
            }

            if (row.Date < context.PeriodFrom || row.Date > context.PeriodUntil)
            {
                problems.Add(
                    $"Absence row of '{row.AgentId}' on {row.Date:yyyy-MM-dd} lies outside the period "
                    + $"{context.PeriodFrom:yyyy-MM-dd}..{context.PeriodUntil:yyyy-MM-dd}. The engine clips break "
                    + "hours to the period and never plans outside it, so such a row can neither block nor credit "
                    + "anything — it would only look like it does.");
            }

            if (row.Hours < 0m)
            {
                problems.Add(
                    $"Absence row of '{row.AgentId}' on {row.Date:yyyy-MM-dd} declares {row.Hours} hours. Negative "
                    + "hours are not a state any Break row can hold.");
            }
        }

        foreach (var group in _rows
                     .GroupBy(r => (r.AgentId, r.Date))
                     .Where(g => g.Count() > 1))
        {
            problems.Add(
                $"'{group.Key.AgentId}' carries {group.Count()} absence rows on {group.Key.Date:yyyy-MM-dd}. The "
                + "engine accumulates break hours per AGENT without keying on the date, so every row of that day is "
                + "credited again — the day would pay twice while blocking once.");
        }

        return problems;
    }
}
