// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis;

/// <summary>
/// Measures the three families that only exist once a scenario carries day shifts, master-data shift
/// preferences or per-day schedule commands: <c>dayShift</c>, <c>preferences</c> and the
/// schedule-command half of <c>keyword</c>. Kept beside
/// <see cref="AutofillPlanAnalyzer"/> rather than inside it for the same reason
/// <see cref="EligibilityAnalyzer"/> is: the measurement decisions below are contestable and belong
/// where they can be read together.
/// <para>
/// Every scan here reads the FINISHED PLAN against the FIXTURE input, never the engine's own violation
/// list. For the blacklist that is not a preference but a necessity — a blacklisted assignment is not
/// a <c>ViolationKind</c> at all, so the engine reports none and a plan full of them would look clean.
/// For the commands it is a decision: the engine does score keyword violations, but nothing rejects a
/// plan for having them, so its list says what the fitness disliked and not what the plan contains.
/// </para>
/// <para>
/// SCOPE. Day-shift shares, preference satisfaction and the command windows count IN-PERIOD
/// assignments, because they ask what this run planned. The two violation scans additionally cover the
/// fixed previous month and flag those entries as carry-in: a carry-in day that breaks a blacklist or
/// a command is a FIXTURE contradiction the engine could not repair even in principle, since a locked
/// assignment is exempt from stage 0 — and a scan that silently skipped it would hide the mistake in
/// exactly the place the fixture author is most likely to make it.
/// </para>
/// </summary>
public static class PreferenceCommandAnalyzer
{
    /// <summary>Reason text of a false positive caused by a broken rest time.</summary>
    public const string RestTimeRule = "rest time";

    /// <summary>Reason text of a false positive caused by a night shift followed by an early one.</summary>
    public const string NightToEarlyRule = "night followed by early";

    /// <summary>Reason text of a false positive caused by two conflicting assignments on one day.</summary>
    public const string DoubleBookingRule = "conflicting assignments on one day";

    /// <summary>Reason text of a false positive that also stands on a blacklisted shift.</summary>
    public const string BlacklistRule = "blacklisted shift";

    /// <summary>Reason text of a false positive that also breaks a schedule command.</summary>
    public const string ScheduleCommandRule = "schedule command";

    /// <summary>
    /// The attribution rule of the control-group diff, stated once and written into every artifact. It
    /// is deliberately narrow and deliberately not widened until the unexplained count reaches zero: a
    /// rule that explains every change explains nothing, and tuning it against the measurement would
    /// be fitting the test to the implementation by another name.
    /// </summary>
    public const string DiffAttributionRule =
        "A changed slot is attributed DIRECTLY when a schedule command or a preference names one of the two "
        + "employees on exactly this slot and this day — the command forbids the slot's class to the employee the "
        + "control run used or to the one the treated run used, or a blacklist or Preferred entry names the slot's "
        + "shift reference for one of them. It is attributed INDIRECTLY (knock-on) when neither employee is named "
        + "on this slot but at least one of them is constrained somewhere else in the period, because work displaced "
        + "by a direct change has to land somewhere. Everything else counts as UNEXPLAINED. A nonzero unexplained "
        + "count is a finding to be reported, never a reason to loosen this rule.";

    /// <summary>
    /// The day-shift view of a plan: every assignment that staffs a day shift, and the day-shift share
    /// of every employee. Empty for a scenario that cuts no day shift.
    /// </summary>
    /// <param name="shiftsWithCarryIn">Shifts per employee, previous month included</param>
    /// <param name="definition">Scenario that produced the plan</param>
    public static DayShiftMetrics BuildDayShift(
        IReadOnlyDictionary<string, List<PlannedShift>> shiftsWithCarryIn, AutofillScenarioDefinition definition)
    {
        if (!definition.HasDayShifts)
        {
            return DayShiftMetrics.Empty;
        }

        var preferences = definition.ShiftPreferences;
        var assignments = shiftsWithCarryIn
            .SelectMany(entry => entry.Value)
            .Where(s => s.IsDayShift)
            .OrderBy(s => s.Date)
            .ThenBy(s => s.Order)
            .ThenBy(s => s.Employee, StringComparer.Ordinal)
            .Select(s => new DayShiftAssignment(
                s.Date,
                s.Order,
                s.Employee,
                s.ShiftRefId,
                preferences?.IsPreferred(s.Employee, s.ShiftRefId) ?? false,
                s.IsCarryIn))
            .ToList();

        var shares = new List<DayShiftShare>();
        foreach (var employee in definition.EmployeesInListOrder)
        {
            var inPeriod = InPeriod(shiftsWithCarryIn, employee, definition);
            var dayShifts = inPeriod.Count(s => s.IsDayShift);
            shares.Add(new DayShiftShare(
                employee,
                dayShifts,
                inPeriod.Count,
                inPeriod.Count == 0 ? 0 : (double)dayShifts / inPeriod.Count,
                PrefersAnyDayShift(employee, definition)));
        }

        return new DayShiftMetrics(assignments, shares);
    }

    /// <summary>
    /// What the plan made of the shift preferences: the blacklist entries it broke, the preferred
    /// assignments that take part in a hard finding, and how often each employee actually got a shift
    /// he asked for. Empty for a scenario without preferences.
    /// </summary>
    /// <param name="shiftsWithCarryIn">Shifts per employee, previous month included</param>
    /// <param name="definition">Scenario that produced the plan</param>
    /// <param name="legality">Legality measurement of the same plan; supplies the hard findings</param>
    /// <param name="coverage">Coverage measurement of the same plan; supplies the conflicting assignments</param>
    /// <param name="commandViolations">Schedule-command violations of the same plan</param>
    public static PreferenceMetrics BuildPreferences(
        IReadOnlyDictionary<string, List<PlannedShift>> shiftsWithCarryIn,
        AutofillScenarioDefinition definition,
        LegalityMetrics legality,
        CoverageMetrics coverage,
        IReadOnlyList<ScheduleCommandViolation> commandViolations)
    {
        var preferences = definition.ShiftPreferences;
        if (preferences is null || preferences.IsEmpty)
        {
            return PreferenceMetrics.Empty;
        }

        var blacklistViolations = shiftsWithCarryIn
            .SelectMany(entry => entry.Value)
            .Where(s => preferences.IsBlacklisted(s.Employee, s.ShiftRefId))
            .OrderBy(s => definition.ListRankOf(s.Employee))
            .ThenBy(s => s.Date)
            .ThenBy(s => s.Order)
            .Select(s => new PreferenceBlacklistViolation(
                s.Employee, s.Date, s.Order, s.SlotKind, s.ShiftRefId, s.IsCarryIn))
            .ToList();

        var hardFindings = BuildHardFindings(legality, coverage, blacklistViolations, commandViolations);
        var falsePositives = new List<PreferenceFalsePositive>();
        foreach (var shift in shiftsWithCarryIn
                     .SelectMany(entry => entry.Value)
                     .Where(s => preferences.IsPreferred(s.Employee, s.ShiftRefId))
                     .OrderBy(s => definition.ListRankOf(s.Employee))
                     .ThenBy(s => s.Date)
                     .ThenBy(s => s.Order))
        {
            if (hardFindings.TryGetValue((shift.Employee, shift.Date), out var broken))
            {
                falsePositives.AddRange(broken.Select(rule => new PreferenceFalsePositive(
                    shift.Employee, shift.Date, shift.Order, shift.SlotKind, shift.ShiftRefId, rule)));
            }
        }

        var satisfaction = new List<PreferenceSatisfaction>();
        foreach (var employee in definition.EmployeesInListOrder)
        {
            var inPeriod = InPeriod(shiftsWithCarryIn, employee, definition);
            var preferred = inPeriod.Count(s => preferences.IsPreferred(employee, s.ShiftRefId));
            satisfaction.Add(new PreferenceSatisfaction(
                employee,
                preferred,
                inPeriod.Count,
                inPeriod.Count == 0 ? 0 : (double)preferred / inPeriod.Count));
        }

        return new PreferenceMetrics(blacklistViolations, falsePositives, satisfaction);
    }

    /// <summary>
    /// Every assignment that stands inside a schedule-command window and breaks its keyword. Empty for
    /// a scenario without commands.
    /// </summary>
    /// <param name="shiftsWithCarryIn">Shifts per employee, previous month included</param>
    /// <param name="definition">Scenario that produced the plan</param>
    public static IReadOnlyList<ScheduleCommandViolation> BuildScheduleCommandViolations(
        IReadOnlyDictionary<string, List<PlannedShift>> shiftsWithCarryIn, AutofillScenarioDefinition definition)
    {
        var commands = definition.ScheduleCommands;
        if (commands is null || commands.IsEmpty)
        {
            return [];
        }

        var violations = new List<ScheduleCommandViolation>();
        foreach (var shift in shiftsWithCarryIn
                     .SelectMany(entry => entry.Value)
                     .OrderBy(s => definition.ListRankOf(s.Employee))
                     .ThenBy(s => s.Date)
                     .ThenBy(s => s.Order))
        {
            foreach (var command in commands.CommandsOn(shift.Employee, shift.Date)
                         .Where(c => c.ForbidsKind(shift.SlotKind)))
            {
                violations.Add(new ScheduleCommandViolation(
                    shift.Employee,
                    shift.Date,
                    command.Keyword,
                    shift.Kind,
                    shift.SlotKind,
                    shift.Order,
                    shift.IsCarryIn));
            }
        }

        return violations;
    }

    /// <summary>
    /// Every command window of the fixture with what the plan placed inside it. The list exists so an
    /// EMPTY window stays checkable: a honoured FREE window produces neither a violation nor an
    /// assignment, and only the window itself can distinguish that from a window nobody applied.
    /// </summary>
    /// <param name="shiftsWithCarryIn">Shifts per employee, previous month included</param>
    /// <param name="definition">Scenario that produced the plan</param>
    public static IReadOnlyList<ScheduleCommandWindow> BuildScheduleCommandWindows(
        IReadOnlyDictionary<string, List<PlannedShift>> shiftsWithCarryIn, AutofillScenarioDefinition definition)
    {
        var commands = definition.ScheduleCommands;
        if (commands is null || commands.IsEmpty)
        {
            return [];
        }

        var windows = new List<ScheduleCommandWindow>();
        foreach (var command in commands.Commands
                     .OrderBy(c => definition.ListRankOf(c.AgentId))
                     .ThenBy(c => c.FromInclusive)
                     .ThenBy(c => c.Keyword))
        {
            var owned = shiftsWithCarryIn.TryGetValue(command.AgentId, out var found)
                ? found
                : [];

            var inside = owned
                .Where(s => command.Covers(s.Date))
                .OrderBy(s => s.Date)
                .ThenBy(s => s.Order)
                .Select(s => new ScheduleCommandWindowAssignment(
                    s.Date, s.Order, s.SlotKind, s.Kind, command.ForbidsKind(s.SlotKind)))
                .ToList();

            windows.Add(new ScheduleCommandWindow(
                command.AgentId, command.Keyword, command.FromInclusive, command.UntilInclusive, inside));
        }

        return windows;
    }

    /// <summary>
    /// Compares the treated run against the control run slot by slot and attributes every difference,
    /// following <see cref="DiffAttributionRule"/>. The treated run is the one carrying the
    /// preferences and commands; the control run is the same fixture without them.
    /// </summary>
    /// <param name="treatmentPlan">Plan of the run under the restriction</param>
    /// <param name="treatmentDefinition">Scenario that produced it; supplies the preferences and commands</param>
    /// <param name="controlPlan">Plan of the run without the restriction</param>
    /// <param name="controlDefinition">Scenario that produced the control run; must cover the same period</param>
    public static AttributedAssignmentDiff AttributeDiff(
        CoreScenario treatmentPlan,
        AutofillScenarioDefinition treatmentDefinition,
        CoreScenario controlPlan,
        AutofillScenarioDefinition controlDefinition)
    {
        var raw = AutofillPlanAnalyzer.DiffAssignments(
            controlPlan, controlDefinition, treatmentPlan, treatmentDefinition);

        var attributed = new List<AttributedChangedAssignment>();
        foreach (var change in raw.ChangedAssignments)
        {
            attributed.Add(new AttributedChangedAssignment(
                change.Date,
                change.ShiftType,
                change.SlotKind,
                change.Order,
                change.EmployeeTreatment,
                change.EmployeeBaseline,
                Attribute(
                    treatmentDefinition,
                    change.Date,
                    change.ShiftRefId,
                    change.EmployeeTreatment,
                    change.EmployeeBaseline)));
        }

        return new AttributedAssignmentDiff(
            attributed,
            attributed.Count(a => a.AttributedTo == DiffAttribution.Unexplained),
            DiffAttributionRule);
    }

    private static DiffAttribution Attribute(
        AutofillScenarioDefinition definition,
        DateOnly date,
        Guid shiftRefId,
        string? employeeTreatment,
        string? employeeControl)
    {
        var commands = definition.ScheduleCommands;
        var preferences = definition.ShiftPreferences;
        var slotKind = AutofillShiftCatalog.SlotKindOf(shiftRefId);

        if (commands is not null && slotKind is not null)
        {
            if (employeeControl is not null && commands.Forbids(employeeControl, date, slotKind.Value))
            {
                return DiffAttribution.KeywordRemovedEmployee;
            }

            if (employeeTreatment is not null && commands.Forbids(employeeTreatment, date, slotKind.Value))
            {
                return DiffAttribution.KeywordWouldForbidReplacement;
            }
        }

        if (preferences is not null)
        {
            if (employeeControl is not null && preferences.IsBlacklisted(employeeControl, shiftRefId))
            {
                return DiffAttribution.BlacklistRemovedEmployee;
            }

            if (employeeTreatment is not null && preferences.IsBlacklisted(employeeTreatment, shiftRefId))
            {
                return DiffAttribution.BlacklistWouldForbidReplacement;
            }

            if (employeeTreatment is not null && preferences.IsPreferred(employeeTreatment, shiftRefId))
            {
                return DiffAttribution.PreferenceGained;
            }

            if (employeeControl is not null && preferences.IsPreferred(employeeControl, shiftRefId))
            {
                return DiffAttribution.PreferenceLost;
            }
        }

        return IsConstrainedAnywhere(definition, employeeTreatment) || IsConstrainedAnywhere(definition, employeeControl)
            ? DiffAttribution.KnockOnOfConstrainedEmployee
            : DiffAttribution.Unexplained;
    }

    private static bool IsConstrainedAnywhere(AutofillScenarioDefinition definition, string? employee)
    {
        if (employee is null)
        {
            return false;
        }

        var byCommand = definition.ScheduleCommands?.Employees().Contains(employee, StringComparer.Ordinal) ?? false;
        var byPreference = definition.ShiftPreferences?.Preferences
            .Any(p => string.Equals(p.AgentId, employee, StringComparison.Ordinal)) ?? false;
        return byCommand || byPreference;
    }

    private static Dictionary<(string Employee, DateOnly Date), List<string>> BuildHardFindings(
        LegalityMetrics legality,
        CoverageMetrics coverage,
        IReadOnlyList<PreferenceBlacklistViolation> blacklistViolations,
        IReadOnlyList<ScheduleCommandViolation> commandViolations)
    {
        var findings = new Dictionary<(string, DateOnly), List<string>>();

        foreach (var violation in legality.RestViolations)
        {
            Add(findings, violation.Employee, violation.DateFrom, RestTimeRule);
            Add(findings, violation.Employee, violation.DateTo, RestTimeRule);
        }

        foreach (var violation in legality.NightToEarlyViolations)
        {
            Add(findings, violation.Employee, violation.Date, NightToEarlyRule);
            Add(findings, violation.Employee, violation.Date.AddDays(1), NightToEarlyRule);
        }

        foreach (var booking in coverage.DoubleBookings)
        {
            Add(findings, booking.Employee, booking.Date, DoubleBookingRule);
        }

        foreach (var violation in blacklistViolations)
        {
            Add(findings, violation.Employee, violation.Date, BlacklistRule);
        }

        foreach (var violation in commandViolations)
        {
            Add(findings, violation.Employee, violation.Date, ScheduleCommandRule);
        }

        return findings;
    }

    private static void Add(
        Dictionary<(string, DateOnly), List<string>> findings, string employee, DateOnly date, string rule)
    {
        if (!findings.TryGetValue((employee, date), out var rules))
        {
            rules = [];
            findings[(employee, date)] = rules;
        }

        if (!rules.Contains(rule, StringComparer.Ordinal))
        {
            rules.Add(rule);
        }
    }

    private static List<PlannedShift> InPeriod(
        IReadOnlyDictionary<string, List<PlannedShift>> shiftsWithCarryIn,
        string employee,
        AutofillScenarioDefinition definition)
        => shiftsWithCarryIn.TryGetValue(employee, out var found)
            ? found.Where(s => !s.IsCarryIn && s.Date >= definition.PeriodFrom && s.Date <= definition.PeriodUntil).ToList()
            : [];

    private static bool PrefersAnyDayShift(string employee, AutofillScenarioDefinition definition)
        => definition.ShiftPreferences?.Preferences.Any(p =>
            string.Equals(p.AgentId, employee, StringComparison.Ordinal)
            && p.Kind == AutofillShiftKind.Day
            && p.Preference == ShiftPreferenceKind.Preferred) ?? false;
}
