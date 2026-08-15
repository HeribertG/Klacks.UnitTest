// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.Autofill.Fixtures;

/// <summary>
/// The per-day planning commands of a scenario, in one object that feeds both sides of a run: the
/// engine gets <c>CoreWizardContext.ScheduleCommands</c> from it, and the analyzer reads the same
/// object to decide which assignment stands inside which window. One source, so a measurement can
/// never judge against a different command list than the one the engine planned with.
/// <para>
/// These are the engine's OWN window keywords — Free, OnlyEarly, OnlyLate, OnlyNight and their
/// negations — and not the qualification keywords scenario 3 simulates through
/// <c>IneligibleAssignments</c>. The two mechanisms share a word and nothing else: a qualification
/// keyword says who MAY hold a shift, a schedule command says what a named employee's named day may
/// contain. Both may be active at once, and this input never touches the other one's field.
/// </para>
/// </summary>
public sealed class AutofillScheduleCommandInput
{
    private readonly List<AutofillScheduleCommand> _commands;

    /// <param name="commands">Command windows of the scenario</param>
    public AutofillScheduleCommandInput(IEnumerable<AutofillScheduleCommand> commands)
    {
        _commands = [.. commands];
    }

    /// <summary>All windows in declaration order.</summary>
    public IReadOnlyList<AutofillScheduleCommand> Commands => _commands;

    /// <summary>True when the scenario declares no command at all.</summary>
    public bool IsEmpty => _commands.Count == 0;

    /// <summary>The engine field: the windows expanded to one row per employee and day.</summary>
    public IReadOnlyList<CoreScheduleCommand> ToCoreCommands()
        => _commands
            .SelectMany(c => c.ToCore())
            .OrderBy(c => c.AgentId, StringComparer.Ordinal)
            .ThenBy(c => c.Date)
            .ThenBy(c => c.Keyword)
            .ToList();

    /// <summary>Windows covering that employee and day; empty when the day carries no command.</summary>
    /// <param name="agentId">Employee identifier</param>
    /// <param name="date">Calendar day to test</param>
    public IReadOnlyList<AutofillScheduleCommand> CommandsOn(string agentId, DateOnly date)
        => _commands
            .Where(c => string.Equals(c.AgentId, agentId, StringComparison.Ordinal) && c.Covers(date))
            .ToList();

    /// <summary>True when at least one command of that employee and day forbids the given slot kind.</summary>
    /// <param name="agentId">Employee identifier</param>
    /// <param name="date">Calendar day of the assignment</param>
    /// <param name="kind">Slot kind of the assignment; the day shift is judged as late</param>
    public bool Forbids(string agentId, DateOnly date, AutofillShiftKind kind)
        => CommandsOn(agentId, date).Any(c => c.ForbidsKind(kind));

    /// <summary>Employees carrying at least one command, in ordinal order.</summary>
    public IReadOnlyList<string> Employees()
        => _commands
            .Select(c => c.AgentId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Checks the input against the scenario it is attached to. Three mistakes a fixture can make here
    /// would otherwise pass silently and produce a measurement of nothing: naming an employee the
    /// scenario does not have, placing a window outside the planning period, and declaring
    /// <c>NotFree</c>, which is a member of the engine's enum that no switch in the engine handles and
    /// which therefore has no effect whatsoever.
    /// </summary>
    /// <param name="context">Assembled engine context of the scenario</param>
    /// <param name="employees">Employee ids of the scenario</param>
    public IReadOnlyList<string> ValidationProblems(CoreWizardContext context, IReadOnlyList<string> employees)
    {
        var problems = new List<string>();
        var known = employees.ToHashSet(StringComparer.Ordinal);

        foreach (var command in _commands)
        {
            if (!known.Contains(command.AgentId))
            {
                problems.Add(
                    $"Schedule command names employee '{command.AgentId}', who is not part of the scenario.");
            }

            if (command.UntilInclusive < command.FromInclusive)
            {
                problems.Add(
                    $"Schedule command {command.Keyword} of '{command.AgentId}' ends on "
                    + $"{command.UntilInclusive:yyyy-MM-dd}, before it starts on {command.FromInclusive:yyyy-MM-dd}.");
            }

            if (command.FromInclusive < context.PeriodFrom || command.UntilInclusive > context.PeriodUntil)
            {
                problems.Add(
                    $"Schedule command {command.Keyword} of '{command.AgentId}' spans "
                    + $"{command.FromInclusive:yyyy-MM-dd}..{command.UntilInclusive:yyyy-MM-dd}, which reaches outside "
                    + $"the period {context.PeriodFrom:yyyy-MM-dd}..{context.PeriodUntil:yyyy-MM-dd}. Days outside the "
                    + "period are never planned, so that part of the window could not constrain anything.");
            }

            if (command.Keyword == ScheduleCommandKeyword.NotFree)
            {
                problems.Add(
                    $"Schedule command of '{command.AgentId}' uses NotFree. The keyword exists in the engine's enum "
                    + "but no switch in the engine handles it — Stage0HardConstraintChecker, SlotConstraintFilter, "
                    + "PlanConstraintChecker and MaxPossibleCalculator all fall through to their default arm — so it "
                    + "constrains nothing and a fixture that relies on it measures nothing.");
            }
        }

        foreach (var group in _commands
                     .SelectMany(c => c.Days().Select(d => (c.AgentId, Date: d, c.Keyword)))
                     .GroupBy(e => (e.AgentId, e.Date))
                     .Where(g => g.Select(e => e.Keyword).Distinct().Count() > 1))
        {
            problems.Add(
                $"'{group.Key.AgentId}' carries several different keywords on {group.Key.Date:yyyy-MM-dd}: "
                + $"{string.Join(", ", group.Select(e => e.Keyword.ToString()).Distinct().OrderBy(k => k, StringComparer.Ordinal))}. "
                + "The engine vetoes on the FIRST matching command it walks, so the effective rule would depend on "
                + "list order rather than on the specification.");
        }

        return problems;
    }
}
