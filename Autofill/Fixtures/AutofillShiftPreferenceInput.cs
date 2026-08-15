// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.Autofill.Fixtures;

/// <summary>
/// The shift preferences of a scenario, in one object that feeds both sides of a run: the engine gets
/// <c>CoreWizardContext.ShiftPreferences</c> from it, and the analyzer reads the same object to decide
/// which assignments satisfy a preference and which violate a blacklist. One source, so a measurement
/// can never judge against a different preference list than the one the engine planned with — the same
/// arrangement <see cref="AutofillEligibilityInput"/> uses for the ban list.
/// <para>
/// Engine reality this wraps, verified on the code: a preference is a
/// <c>CoreShiftPreference(string AgentId, Guid ShiftRefId, ShiftPreferenceKind Kind)</c>. Blacklist is
/// a HARD stage-0 veto in the auction (<c>Stage0HardConstraintChecker</c>, line 176 calling
/// IsBlacklistedShift at 274-292); Preferred is a SOFT term only, reaching the run through
/// <c>Stage1SoftConstraintChecker.CheckPreferredShift</c> and <c>MotivationFormula</c>, plus the
/// stage-3 fitness weight. Nothing here can make a preference hard, and nothing should try to.
/// </para>
/// </summary>
public sealed class AutofillShiftPreferenceInput
{
    private readonly List<AutofillShiftPreference> _preferences;

    /// <param name="preferences">Preference entries of the scenario</param>
    public AutofillShiftPreferenceInput(IEnumerable<AutofillShiftPreference> preferences)
    {
        _preferences = [.. preferences];
    }

    /// <summary>All entries in declaration order.</summary>
    public IReadOnlyList<AutofillShiftPreference> Preferences => _preferences;

    /// <summary>True when the scenario declares no preference at all.</summary>
    public bool IsEmpty => _preferences.Count == 0;

    /// <summary>The engine field: one record per entry.</summary>
    public IReadOnlyList<CoreShiftPreference> ToCorePreferences()
        => _preferences.Select(p => p.ToCore()).ToList();

    /// <summary>True when the employee prefers that shift.</summary>
    /// <param name="agentId">Employee identifier</param>
    /// <param name="shiftRefId">Shift reference id of the slot</param>
    public bool IsPreferred(string agentId, Guid shiftRefId)
        => Matches(agentId, shiftRefId, ShiftPreferenceKind.Preferred);

    /// <summary>True when the employee is blacklisted from that shift.</summary>
    /// <param name="agentId">Employee identifier</param>
    /// <param name="shiftRefId">Shift reference id of the slot</param>
    public bool IsBlacklisted(string agentId, Guid shiftRefId)
        => Matches(agentId, shiftRefId, ShiftPreferenceKind.Blacklist);

    /// <summary>Employees carrying at least one entry of the given kind, in ordinal order.</summary>
    /// <param name="kind">Preferred or Blacklist</param>
    public IReadOnlyList<string> EmployeesWith(ShiftPreferenceKind kind)
        => _preferences
            .Where(p => p.Preference == kind)
            .Select(p => p.AgentId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Checks the input against the scenario it is attached to. An entry naming an unknown employee or
    /// a shift the scenario never demands is a fixture mistake that would otherwise pass silently: the
    /// engine simply never matches it, and the assertion built on it would measure an effect that was
    /// never applied.
    /// </summary>
    /// <param name="context">Assembled engine context of the scenario</param>
    /// <param name="employees">Employee ids of the scenario</param>
    public IReadOnlyList<string> ValidationProblems(CoreWizardContext context, IReadOnlyList<string> employees)
    {
        var problems = new List<string>();
        var known = employees.ToHashSet(StringComparer.Ordinal);
        var demanded = context.Shifts
            .Select(s => Guid.TryParse(s.Id, out var id) ? id : Guid.Empty)
            .ToHashSet();

        foreach (var preference in _preferences)
        {
            if (!known.Contains(preference.AgentId))
            {
                problems.Add(
                    $"Shift preference names employee '{preference.AgentId}', who is not part of the scenario.");
            }

            if (!demanded.Contains(preference.ShiftRefId))
            {
                problems.Add(
                    $"Shift preference of '{preference.AgentId}' addresses order "
                    + $"{preference.OrderIndex.ToString(CultureInfo.InvariantCulture)} / {preference.Kind} "
                    + $"({preference.ShiftRefId}), which the scenario never demands. The engine would never match it, "
                    + "so the preference would have no effect and any assertion about it would measure nothing.");
            }
        }

        foreach (var group in _preferences.GroupBy(p => (p.AgentId, p.ShiftRefId)).Where(g => g.Select(p => p.Preference).Distinct().Count() > 1))
        {
            problems.Add(
                $"'{group.Key.AgentId}' both prefers and blacklists {group.Key.ShiftRefId}. The engine reads the "
                + "blacklist as a hard veto and the preference as a soft bonus, so the pair is contradictory input "
                + "rather than a graded opinion.");
        }

        return problems;
    }

    private bool Matches(string agentId, Guid shiftRefId, ShiftPreferenceKind kind)
        => _preferences.Any(p =>
            string.Equals(p.AgentId, agentId, StringComparison.Ordinal)
            && p.ShiftRefId == shiftRefId
            && p.Preference == kind);
}
