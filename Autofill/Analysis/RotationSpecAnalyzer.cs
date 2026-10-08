// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Test oracle for the owner's rotation definition of 2026-10-08 (tests/autofill/SPEC-ROTATION-2026-10-08.md). It is a
/// separate implementation on purpose: it must not call the engines' rotation code, otherwise the guard would measure
/// itself. Rules as measured here:
/// <list type="bullet">
/// <item>A block is a run of shifts whose rest to the previous shift is below 48 hours (end to start, in hours).</item>
/// <item>A block is pure when all its shifts have the same shift class (the day shift counts as late).</item>
/// <item>The previous block hands over its LAST shift class; the new block is judged by its FIRST shift class.</item>
/// <item>Ideal successor: the next class in early, late, night, early, skipping classes that are not allowed; after a
/// pause of at least 7 free calendar days the first allowed class from early.</item>
/// <item>A class is not allowed when the employee may not work a single shift of it on any day of the new block for a
/// plan-independent hard reason: no shift work (only early is open, as the engine's veto does today; owner question G1
/// is open), a day directive, or every slot of that class on the day being ineligible or blacklisted. A day without a
/// slot of that class is "no demand" and does not make the class disallowed. At most one allowed class: the change
/// owes no rotation and is skipped.</item>
/// <item>A non-ideal change is forced when, on the new block's first day, the ideal class is closed to the employee
/// by a hard rule ("hardRule"), or when no single swap of the block's first shift with a shift of the ideal class
/// held by another employee that day respects the hard rules of both ("coverage" — eligibility, blacklist, day
/// directive, shift work, rest against both employees' other shifts). A day without a shift of the ideal class is
/// "coverage" too. Any feasible swap makes the change an unforced deviation. Two caveats, both deliberate: chains
/// of several swaps are not searched, and whether the swap hurts the other employee's own rotation is not judged —
/// the count says "the plan could have been different here at no hard-rule cost", not "it would have been
/// better overall".</item>
/// </list>
/// </summary>

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis;

public static class RotationSpecAnalyzer
{
    public const double BlockBoundaryRestHours = 48;
    public const int LongPauseFreeDays = 7;

    public const string CauseHardRule = "hardRule";
    public const string CauseCoverage = "coverage";
    public const string CauseUnforced = "unforced";

    private static readonly AutofillShiftKind[] Cycle =
        [AutofillShiftKind.Early, AutofillShiftKind.Late, AutofillShiftKind.Night];

    /// <summary>Measures the plan.</summary>
    /// <param name="shiftsByEmployee">All shifts per employee, carry-in included, sorted by start</param>
    /// <param name="definition">Scenario that produced the plan</param>
    public static RotationSpecMetrics Measure(
        IReadOnlyDictionary<string, List<PlannedShift>> shiftsByEmployee,
        AutofillScenarioDefinition definition)
    {
        var rules = new HardRules(definition, shiftsByEmployee);

        var blockCount = 0;
        var pureCount = 0;
        var transitions = 0;
        var ideal = 0;
        var skipped = 0;
        var restarts = 0;
        var deviations = new List<RotationSpecDeviation>();

        foreach (var employee in definition.EmployeesInListOrder)
        {
            var shifts = shiftsByEmployee.TryGetValue(employee, out var found) ? found : [];
            var blocks = BlocksOf(shifts);

            for (var b = 0; b < blocks.Count; b++)
            {
                var block = blocks[b];
                if (block[^1].Date < definition.PeriodFrom)
                {
                    continue;
                }

                blockCount++;
                if (block.Select(s => ClassOf(s)).Distinct().Count() == 1)
                {
                    pureCount++;
                }

                if (b == 0 || block[0].Date < definition.PeriodFrom)
                {
                    continue;
                }

                var previous = blocks[b - 1];
                var days = block.Select(s => s.Date).Where(d => d >= definition.PeriodFrom).Distinct().ToList();
                var allowed = Cycle.Where(kind => !days.All(day => rules.IsClosed(employee, kind, day))).ToList();
                if (allowed.Count <= 1)
                {
                    skipped++;
                    continue;
                }

                var longPause = FreeDaysBetween(previous[^1], block[0]) >= LongPauseFreeDays;
                if (longPause)
                {
                    restarts++;
                }

                var from = ClassOf(previous[^1]);
                var to = ClassOf(block[0]);
                var idealKind = longPause ? allowed[0] : SuccessorOf(from, allowed);

                transitions++;
                if (to == idealKind)
                {
                    ideal++;
                    continue;
                }

                var firstDay = block[0].Date;
                var cause = rules.IsClosed(employee, idealKind, firstDay)
                    ? CauseHardRule
                    : rules.HasFeasibleSwap(block[0], idealKind) ? CauseUnforced : CauseCoverage;
                deviations.Add(new RotationSpecDeviation(
                    employee, firstDay, from, to, idealKind, cause != CauseUnforced, cause));
            }
        }

        var forced = deviations.Count(d => d.Forced);
        return new RotationSpecMetrics(
            BlockCount: blockCount,
            PureBlockCount: pureCount,
            BlockPurity: blockCount == 0 ? 1 : (double)pureCount / blockCount,
            TransitionCount: transitions,
            IdealTransitionCount: ideal,
            IdealTransitionRate: transitions == 0 ? 1 : (double)ideal / transitions,
            ForcedDeviationCount: forced,
            UnforcedDeviations: deviations.Count - forced,
            SingleKindSkipCount: skipped,
            LongPauseRestartCount: restarts,
            Deviations: deviations);
    }

    internal static List<List<PlannedShift>> BlocksOf(IReadOnlyList<PlannedShift> shifts)
    {
        var blocks = new List<List<PlannedShift>>();
        foreach (var shift in shifts.OrderBy(s => s.StartAt))
        {
            if (blocks.Count == 0 || (shift.StartAt - blocks[^1][^1].EndAt).TotalHours >= BlockBoundaryRestHours)
            {
                blocks.Add([]);
            }

            blocks[^1].Add(shift);
        }

        return blocks;
    }

    internal static AutofillShiftKind SuccessorOf(AutofillShiftKind from, IReadOnlyList<AutofillShiftKind> allowed)
    {
        var index = Array.IndexOf(Cycle, from);
        for (var step = 1; step <= Cycle.Length; step++)
        {
            var candidate = Cycle[(index + step) % Cycle.Length];
            if (allowed.Contains(candidate))
            {
                return candidate;
            }
        }

        return from;
    }

    private static int FreeDaysBetween(PlannedShift last, PlannedShift next)
        => next.Date.DayNumber - DateOnly.FromDateTime(last.EndAt).DayNumber - 1;

    private static AutofillShiftKind ClassOf(PlannedShift shift) => AutofillShiftCatalog.ShiftClassOf(shift.Kind);

    /// <summary>The plan-independent hard rules and the slot coverage the oracle needs.</summary>
    private sealed class HardRules
    {
        private readonly Dictionary<(AutofillShiftKind Kind, DateOnly Date), List<(Guid ShiftId, int Required)>> _slots = [];
        private readonly IReadOnlyDictionary<string, List<PlannedShift>> _shiftsByEmployee;
        private readonly Dictionary<string, double> _minRestHours;
        private readonly Dictionary<(string Agent, DateOnly Date), List<ScheduleCommandKeyword>> _commands = [];
        private readonly HashSet<(string Agent, Guid ShiftId)> _blacklist;
        private readonly HashSet<string> _noShiftWork;
        private readonly IReadOnlySet<(string AgentId, Guid ShiftId, DateOnly Date)> _ineligible;

        public HardRules(AutofillScenarioDefinition definition, IReadOnlyDictionary<string, List<PlannedShift>> shiftsByEmployee)
        {
            var context = definition.Context;
            foreach (var shift in context.Shifts)
            {
                var date = DateOnly.ParseExact(shift.Date, AutofillSpecConstants.IsoDateFormat, CultureInfo.InvariantCulture);
                var kind = AutofillShiftCatalog.FromShiftTypeIndex(ShiftTypeInference.FromSpanString(shift.StartTime, shift.EndTime));
                var key = (kind, date);
                if (!_slots.TryGetValue(key, out var list))
                {
                    _slots[key] = list = [];
                }

                list.Add((Guid.Parse(shift.Id), shift.RequiredAssignments));
            }

            _shiftsByEmployee = shiftsByEmployee;
            _minRestHours = context.Agents.ToDictionary(a => a.Id, a => a.MinRestHours, StringComparer.Ordinal);

            foreach (var command in context.ScheduleCommands)
            {
                var key = (command.AgentId, command.Date);
                if (!_commands.TryGetValue(key, out var list))
                {
                    _commands[key] = list = [];
                }

                list.Add(command.Keyword);
            }

            _blacklist = context.ShiftPreferences
                .Where(p => p.Kind == ShiftPreferenceKind.Blacklist)
                .Select(p => (p.AgentId, p.ShiftRefId))
                .ToHashSet();
            _noShiftWork = context.Agents.Where(a => !a.PerformsShiftWork).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            _ineligible = context.IneligibleAssignments;
        }

        public bool IsClosed(string employee, AutofillShiftKind kind, DateOnly date)
        {
            if (_noShiftWork.Contains(employee) && kind != AutofillShiftKind.Early)
            {
                return true;
            }

            if (_commands.TryGetValue((employee, date), out var keywords) && keywords.Any(k => !Allows(k, kind)))
            {
                return true;
            }

            return _slots.TryGetValue((kind, date), out var slots)
                && slots.All(s => _ineligible.Contains((employee, s.ShiftId, date)) || _blacklist.Contains((employee, s.ShiftId)));
        }

        /// <summary>
        /// True when the employee's shift could be swapped with a shift of the ideal class another employee holds on
        /// the same day, both sides respecting the plan-independent hard rules and their rest.
        /// </summary>
        public bool HasFeasibleSwap(PlannedShift own, AutofillShiftKind idealKind)
        {
            foreach (var (other, shifts) in _shiftsByEmployee)
            {
                if (string.Equals(other, own.Employee, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var candidate in shifts.Where(s => !s.IsCarryIn && s.Date == own.Date && ClassOf(s) == idealKind))
                {
                    if (CanTake(own.Employee, candidate, idealKind, own)
                        && CanTake(other, own, ClassOf(own), candidate))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool CanTake(string employee, PlannedShift shift, AutofillShiftKind kind, PlannedShift givenUp)
        {
            if (_noShiftWork.Contains(employee) && kind != AutofillShiftKind.Early)
            {
                return false;
            }

            if (_commands.TryGetValue((employee, shift.Date), out var keywords) && keywords.Any(k => !Allows(k, kind)))
            {
                return false;
            }

            if (_ineligible.Contains((employee, shift.ShiftRefId, shift.Date)) || _blacklist.Contains((employee, shift.ShiftRefId)))
            {
                return false;
            }

            var minRest = _minRestHours.TryGetValue(employee, out var rest) ? rest : 0;
            var others = _shiftsByEmployee.TryGetValue(employee, out var list) ? list : [];
            return others.Where(o => !ReferenceEquals(o, givenUp)).All(o => RestBetween(o, shift) >= minRest);
        }

        private static double RestBetween(PlannedShift a, PlannedShift b)
        {
            if (a.EndAt <= b.StartAt)
            {
                return (b.StartAt - a.EndAt).TotalHours;
            }

            if (b.EndAt <= a.StartAt)
            {
                return (a.StartAt - b.EndAt).TotalHours;
            }

            return double.NegativeInfinity;
        }

        private static bool Allows(ScheduleCommandKeyword keyword, AutofillShiftKind kind) => keyword switch
        {
            ScheduleCommandKeyword.Free => false,
            ScheduleCommandKeyword.OnlyEarly => kind == AutofillShiftKind.Early,
            ScheduleCommandKeyword.NoEarly => kind != AutofillShiftKind.Early,
            ScheduleCommandKeyword.OnlyLate => kind == AutofillShiftKind.Late,
            ScheduleCommandKeyword.NoLate => kind != AutofillShiftKind.Late,
            ScheduleCommandKeyword.OnlyNight => kind == AutofillShiftKind.Night,
            ScheduleCommandKeyword.NoNight => kind != AutofillShiftKind.Night,
            _ => true,
        };
    }
}
