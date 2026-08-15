// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis;

/// <summary>
/// Everything a finished plan says about the booked absences of its scenario, plus the capacity
/// arithmetic that follows from them. Scenario-independent, and inert without absences: every method
/// returns the empty measurement when the definition carries no absence row, so scenarios 1 to 5
/// measure exactly what they measured before this file existed.
/// <para>
/// The three facts every measure here rests on, each read off the engine rather than assumed.
/// (1) The block is a whole calendar DAY and knows no clock: <c>CoreBreakBlocker</c> has no time
/// fields, and <c>IsBlockedByBreak</c> compares dates only.
/// (2) The block is evaluated against the assignment's START date alone —
/// <c>Stage0HardConstraintChecker.cs:100-104</c> derives the date from <c>token.Date</c> and
/// <c>:164</c> tests that date — so a night shift beginning the evening before an absence is not
/// blocked, while the owner rule of 2026-08-14 forbids it. The boundary measure states both.
/// (3) The hours are a CREDIT on the actual side, never a reduction of the target
/// (<c>TokenFitnessEvaluator.cs:222-225</c>), and a row with <c>Hours &lt;= 0</c> credits nothing
/// while still blocking its day (<c>:262</c> skips the credit, not the block).
/// </para>
/// </summary>
public static class AbsenceAnalyzer
{
    /// <summary>Cause label of everything an absence forced; written into the artifacts verbatim.</summary>
    public const string AbsenceCause = "absence";

    /// <summary>Days of a clean rhythm cycle: the contractual work days plus the contractual rest days.</summary>
    private const int RhythmCycleDays = AutofillSpecConstants.MaxWorkDays + AutofillSpecConstants.MinRestDays;

    private const string NoReason = "-";

    private const string ReasonSeparator = "/";

    /// <summary>True when the scenario books at least one absence day.</summary>
    /// <param name="definition">Scenario to test</param>
    public static bool IsActive(AutofillScenarioDefinition definition) => definition.HasAbsences;

    /// <summary>
    /// The free days of the gap between two packages: every calendar day strictly between them that
    /// is NOT an absence day. This is the one place the rule "an absence day is not a free day" is
    /// implemented; the free-block histogram, the ideal share and the free edges all read it, so the
    /// rule cannot hold in one measure and fail in another.
    /// </summary>
    /// <param name="previousEnd">Last day of the earlier package</param>
    /// <param name="nextStart">First day of the later package</param>
    /// <param name="employee">Employee both packages belong to</param>
    /// <param name="definition">Scenario carrying the absence rows</param>
    public static IReadOnlyList<DateOnly> FreeDaysBetween(
        DateOnly previousEnd, DateOnly nextStart, string employee, AutofillScenarioDefinition definition)
    {
        var days = new List<DateOnly>();
        for (var date = previousEnd.AddDays(1); date < nextStart; date = date.AddDays(1))
        {
            if (!definition.Absences.Covers(employee, date))
            {
                days.Add(date);
            }
        }

        return days;
    }

    /// <summary>
    /// Free days of a period edge — the days between the period boundary and the nearest package that
    /// are not absence days. Same rule as <see cref="FreeDaysBetween"/>, applied where nothing bounds
    /// the run on the outside.
    /// </summary>
    /// <param name="from">First day of the edge run, inclusive</param>
    /// <param name="until">Last day of the edge run, inclusive</param>
    /// <param name="employee">Employee the edge belongs to</param>
    /// <param name="definition">Scenario carrying the absence rows</param>
    public static int FreeEdgeDays(
        DateOnly from, DateOnly until, string employee, AutofillScenarioDefinition definition)
    {
        var count = 0;
        for (var date = from; date <= until; date = date.AddDays(1))
        {
            if (!definition.Absences.Covers(employee, date))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Measures the absences against a finished plan.
    /// </summary>
    /// <param name="shiftsByEmployee">Every shift per employee, the fixed previous month included</param>
    /// <param name="packagesByEmployee">Work packages per employee, used for the free-day audit</param>
    /// <param name="definition">Scenario that produced the plan</param>
    public static AbsenceMetrics BuildAbsences(
        IReadOnlyDictionary<string, List<PlannedShift>> shiftsByEmployee,
        IReadOnlyDictionary<string, IReadOnlyList<WorkPackage>> packagesByEmployee,
        AutofillScenarioDefinition definition)
    {
        if (!IsActive(definition))
        {
            return AbsenceMetrics.Empty;
        }

        var absences = definition.Absences;
        var entries = new List<AbsenceWindowEntry>();
        var credit = new SortedDictionary<string, double>(StringComparer.Ordinal);

        foreach (var employee in absences.Employees())
        {
            credit[employee] = (double)absences.CreditHoursOf(employee);
            foreach (var (from, until) in absences.WindowsOf(employee))
            {
                var rows = absences.Rows
                    .Where(r => string.Equals(r.AgentId, employee, StringComparison.Ordinal)
                                && r.Date >= from && r.Date <= until)
                    .ToList();
                entries.Add(new AbsenceWindowEntry(
                    Employee: employee,
                    ListRank: definition.ListRankOf(employee),
                    FromInclusive: from,
                    UntilInclusive: until,
                    Rows: rows.Count,
                    PaidRows: rows.Count(r => r.GrantsCredit),
                    CreditHours: (double)rows.Where(r => r.GrantsCredit).Sum(r => r.Hours),
                    Reason: DescribeReasons(rows)));
            }
        }

        var boundaryCases = BuildBoundaryCases(shiftsByEmployee, definition);

        return new AbsenceMetrics(
            Entries: entries.OrderBy(e => e.ListRank).ThenBy(e => e.FromInclusive).ToList(),
            Rows: absences.Rows.Count,
            Violations: BuildViolations(shiftsByEmployee, definition),
            BoundaryCases: boundaryCases,
            DaysCountedAsFree: AnyAbsenceDayCountedAsFree(packagesByEmployee, definition),
            RedundantWithKeyword: BuildRedundancies(definition),
            CreditHoursByEmployee: credit);
    }

    /// <summary>
    /// The capacity arithmetic of the fixture. Two conventions, both stated because the two counts
    /// differ on purpose: <c>SlotsTotal</c> and the absence-window slots subtract absence days only,
    /// while the tightest day also subtracts the days a FREE command closes. The absence is a property
    /// of the roster and belongs in the capacity; the FREE command is a property of one day and is what
    /// makes a single day the tightest one.
    /// </summary>
    /// <param name="definition">Scenario to measure</param>
    public static AvailabilityMetrics BuildAvailability(AutofillScenarioDefinition definition)
    {
        var periodDays = definition.PeriodUntil.DayNumber - definition.PeriodFrom.DayNumber + 1;
        var slotsTotal = (definition.EmployeesInListOrder.Count * periodDays) - definition.Absences.Rows.Count;
        var required = definition.Context.Shifts.Count;
        var maxUnder52 = AutofillSpecConstants.MaxWorkDays / (double)RhythmCycleDays;
        var carriedBy52 = slotsTotal * maxUnder52;
        var forcedExtra = required <= carriedBy52 ? 0 : (int)Math.Ceiling(required - carriedBy52);

        TightestDay? tightest = null;
        for (var date = definition.PeriodFrom; date <= definition.PeriodUntil; date = date.AddDays(1))
        {
            var available = definition.EmployeesInListOrder.Count(e => IsAvailableOn(e, date, definition));
            var ratio = available == 0 ? double.PositiveInfinity : definition.SlotsPerDay / (double)available;
            if (tightest is null || ratio > tightest.Ratio)
            {
                tightest = new TightestDay(date, available, definition.SlotsPerDay, ratio);
            }
        }

        var (windowSlots, windowRequired) = MeasureAbsenceWindows(definition);

        return new AvailabilityMetrics(
            SlotsTotal: slotsTotal,
            RequiredAssignments: required,
            WorkRatioRequired: slotsTotal == 0 ? 0 : required / (double)slotsTotal,
            MaxRatioUnder52: maxUnder52,
            ForcedExtraShifts: forcedExtra,
            TightestDay: tightest,
            AbsenceWindowSlots: windowSlots,
            AbsenceWindowRequired: windowRequired);
    }

    /// <summary>
    /// The hour target of every absent employee, seen the way the engine sees it: the unchanged target
    /// against current hours plus planned hours plus absence credit.
    /// <para>
    /// A shortfall is declared FORCED only when it is provable from the fixture — when even the most
    /// optimistic rhythm over the days the employee still has cannot close the gap. The bound is an
    /// upper bound on purpose: a declaration that rests on an optimistic bound is safe, because a
    /// shortfall that beats even the optimistic bound cannot be the algorithm's fault.
    /// </para>
    /// </summary>
    /// <param name="hours">Planned hours per employee, as the hour metric measured them</param>
    /// <param name="definition">Scenario that produced the plan</param>
    public static IReadOnlyList<CreditTargetEntry> BuildCreditTarget(
        IReadOnlyList<EmployeeHours> hours, AutofillScenarioDefinition definition)
    {
        if (!IsActive(definition))
        {
            return [];
        }

        var agentsById = definition.Context.Agents.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var entries = new List<CreditTargetEntry>();

        foreach (var entry in hours.Where(h => definition.Absences.DaysOf(h.Employee).Count > 0))
        {
            var creditHours = (double)definition.Absences.CreditHoursOf(entry.Employee);
            var currentHours = agentsById.TryGetValue(entry.Employee, out var agent) ? agent.CurrentHours : 0;
            var covered = currentHours + entry.PlannedHours + creditHours;
            var fulfilled = covered + AutofillSpecConstants.MonotonicityEpsilon >= entry.GuaranteedHours;
            var reachable = (MaxReachableShifts(entry.Employee, definition) * AutofillSpecConstants.ShiftHours)
                + currentHours + creditHours;
            var forced = !fulfilled && reachable < entry.GuaranteedHours;

            entries.Add(new CreditTargetEntry(
                Employee: entry.Employee,
                ListRank: entry.ListRank,
                OriginalTarget: entry.GuaranteedHours,
                CreditHours: creditHours,
                PlannedHours: entry.PlannedHours,
                CurrentHours: currentHours,
                CoveredIncludingCredit: covered,
                Fulfilled: fulfilled,
                ForcedShortfallDeclared: forced,
                ShortfallCause: forced ? AbsenceCause : string.Empty));
        }

        return entries.OrderBy(e => e.ListRank).ToList();
    }

    /// <summary>
    /// An upper bound on the shifts one employee can still hold: the days no absence and no FREE
    /// command closes, cut into their consecutive runs, each run filled with the contractual
    /// five-work/two-free rhythm. It ignores every other rule — coverage, rotation, fairness, the
    /// carry-in package already in progress — which is exactly what makes it an upper bound.
    /// </summary>
    /// <param name="employee">Employee identifier</param>
    /// <param name="definition">Scenario carrying the absences and the commands</param>
    public static int MaxReachableShifts(string employee, AutofillScenarioDefinition definition)
    {
        var total = 0;
        var run = 0;
        for (var date = definition.PeriodFrom; date <= definition.PeriodUntil.AddDays(1); date = date.AddDays(1))
        {
            if (date <= definition.PeriodUntil && IsAvailableOn(employee, date, definition))
            {
                run++;
                continue;
            }

            total += WorkableDaysInRun(run);
            run = 0;
        }

        return total;
    }

    /// <summary>Packages an absence cut short, and carried-in packages an absence stopped at the seam.</summary>
    /// <param name="packagesByEmployee">Work packages per employee</param>
    /// <param name="definition">Scenario that produced the plan</param>
    public static IReadOnlyList<AbsenceCut> BuildAbsenceCuts(
        IReadOnlyDictionary<string, IReadOnlyList<WorkPackage>> packagesByEmployee,
        AutofillScenarioDefinition definition)
    {
        if (!IsActive(definition))
        {
            return [];
        }

        var cuts = new List<AbsenceCut>();

        foreach (var employee in definition.EmployeesInListOrder)
        {
            var packages = packagesByEmployee.TryGetValue(employee, out var found) ? found : [];
            foreach (var package in packages)
            {
                var nextDay = package.EndDate.AddDays(1);
                if (!definition.Absences.Covers(employee, nextDay))
                {
                    continue;
                }

                cuts.Add(new AbsenceCut(
                    Employee: employee,
                    PackageStart: package.StartDate,
                    PackageEnd: package.EndDate,
                    LengthDays: package.LengthDays,
                    ShiftType: package.ShiftType,
                    CutAt: nextDay,
                    MissingDays: Math.Max(0, AutofillSpecConstants.MaxWorkDays - package.LengthDays),
                    Forced: true,
                    Cause: AbsenceCause));
            }
        }

        cuts.AddRange(BuildCarryInCuts(packagesByEmployee, definition));
        return cuts.OrderBy(c => definition.ListRankOf(c.Employee)).ThenBy(c => c.PackageEnd).ToList();
    }

    /// <summary>
    /// What the shift class did across each absence window. Reported, never asserted into a direction:
    /// the engine documents no rotation reset after an absence and no owner decision names one.
    /// </summary>
    /// <param name="packagesByEmployee">Work packages per employee</param>
    /// <param name="definition">Scenario that produced the plan</param>
    public static IReadOnlyList<ContinuityAcrossAbsence> BuildContinuity(
        IReadOnlyDictionary<string, IReadOnlyList<WorkPackage>> packagesByEmployee,
        AutofillScenarioDefinition definition)
    {
        if (!IsActive(definition))
        {
            return [];
        }

        var result = new List<ContinuityAcrossAbsence>();
        foreach (var (employee, from, until) in definition.Absences.AllWindows())
        {
            var packages = packagesByEmployee.TryGetValue(employee, out var found) ? found : [];
            var before = packages.Where(p => p.EndDate < from).OrderBy(p => p.EndDate).LastOrDefault();
            var after = packages.Where(p => p.StartDate > until).OrderBy(p => p.StartDate).FirstOrDefault();

            var kindBefore = before is null ? (AutofillShiftKind?)null : ShiftClassOf(before);
            var kindAfter = after is null ? (AutofillShiftKind?)null : ShiftClassOf(after);
            var gapDays = before is null || after is null
                ? 0
                : after.StartDate.DayNumber - before.EndDate.DayNumber - 1;

            result.Add(new ContinuityAcrossAbsence(
                Employee: employee,
                WindowFrom: from,
                WindowUntil: until,
                KindBefore: kindBefore,
                KindAfter: kindAfter,
                GapDays: gapDays,
                ContinuesForward: kindBefore is not null && kindAfter is not null
                    && kindAfter == ForwardSuccessorOf(kindBefore.Value),
                RestartsOnEarly: kindAfter == AutofillShiftKind.Early
                    && kindBefore is not null && kindBefore != AutofillShiftKind.Late));
        }

        return result;
    }

    /// <summary>
    /// True when a date range lies inside, next to or across an absence window of ANY employee — the
    /// test for "this happened where the absence removed capacity". The window is widened by one day
    /// at each end on purpose: a package that ends the day before a holiday begins, or starts the day
    /// after it ends, is caused by that holiday just as much as one inside it.
    /// <para>
    /// Always false for a scenario without absences, which keeps the flag inert for scenarios 1 to 5.
    /// </para>
    /// </summary>
    /// <param name="from">First day of the range</param>
    /// <param name="until">Last day of the range</param>
    /// <param name="definition">Scenario carrying the absence rows</param>
    public static bool TouchesAnyAbsenceWindow(
        DateOnly from, DateOnly until, AutofillScenarioDefinition definition)
        => IsActive(definition)
            && definition.Absences.AllWindows().Any(window =>
                from <= window.Until.AddDays(1) && until >= window.From.AddDays(-1));

    /// <summary>True when the employee is neither absent nor closed by a FREE command on that day.</summary>
    /// <param name="employee">Employee identifier</param>
    /// <param name="date">Calendar day to test</param>
    /// <param name="definition">Scenario carrying absences and commands</param>
    public static bool IsAvailableOn(string employee, DateOnly date, AutofillScenarioDefinition definition)
    {
        if (definition.Absences.Covers(employee, date))
        {
            return false;
        }

        return definition.ScheduleCommands is not { IsEmpty: false }
            || !definition.ScheduleCommands.CommandsOn(employee, date)
                .Any(c => c.Keyword == ScheduleCommandKeyword.Free);
    }

    private static IReadOnlyList<AbsenceViolation> BuildViolations(
        IReadOnlyDictionary<string, List<PlannedShift>> shiftsByEmployee,
        AutofillScenarioDefinition definition)
        => definition.Absences.Employees()
            .SelectMany(employee => ShiftsOf(employee, shiftsByEmployee)
                .Where(shift => definition.Absences.Covers(employee, shift.Date))
                .Select(shift => new AbsenceViolation(
                    Employee: employee,
                    Date: shift.Date,
                    SlotKind: shift.SlotKind,
                    Order: shift.Order,
                    ShiftRefId: shift.ShiftRefId,
                    IsCarryIn: shift.IsCarryIn)))
            .OrderBy(v => definition.ListRankOf(v.Employee))
            .ThenBy(v => v.Date)
            .ToList();

    private static IReadOnlyList<AbsenceBoundaryCase> BuildBoundaryCases(
        IReadOnlyDictionary<string, List<PlannedShift>> shiftsByEmployee,
        AutofillScenarioDefinition definition)
    {
        var cases = new List<AbsenceBoundaryCase>();
        foreach (var (employee, from, until) in definition.Absences.AllWindows())
        {
            foreach (var shift in ShiftsOf(employee, shiftsByEmployee))
            {
                var startsInside = shift.Date >= from && shift.Date <= until;
                var endDate = DateOnly.FromDateTime(EffectiveEnd(shift));
                var endsInside = endDate >= from && endDate <= until;
                if (!startsInside && !endsInside)
                {
                    continue;
                }

                var evaluation = startsInside && endsInside
                    ? AbsenceEdgeEvaluation.Both
                    : startsInside ? AbsenceEdgeEvaluation.Start : AbsenceEdgeEvaluation.End;

                cases.Add(new AbsenceBoundaryCase(
                    Employee: employee,
                    Date: shift.Date,
                    EndsAt: shift.EndAt,
                    SlotKind: shift.SlotKind,
                    Order: shift.Order,
                    WindowFrom: from,
                    WindowUntil: until,
                    EvaluatedAgainst: evaluation,
                    Accepted: true,
                    ViolatesOverlapRule: true));
            }
        }

        return cases
            .OrderBy(c => definition.ListRankOf(c.Employee))
            .ThenBy(c => c.Date)
            .ThenBy(c => c.Order)
            .ToList();
    }

    /// <summary>
    /// The last instant of a shift that still belongs to it. A shift ending exactly at midnight ends
    /// on the previous calendar day, and the night shift 23:00-07:00 genuinely reaches into the next
    /// one; subtracting a tick separates the two without a special case per shift kind.
    /// </summary>
    /// <param name="shift">Shift to measure</param>
    private static DateTime EffectiveEnd(PlannedShift shift)
        => shift.EndAt > shift.StartAt ? shift.EndAt.AddTicks(-1) : shift.StartAt;

    private static IReadOnlyList<AbsenceKeywordRedundancy> BuildRedundancies(AutofillScenarioDefinition definition)
    {
        if (definition.ScheduleCommands is not { IsEmpty: false })
        {
            return [];
        }

        var result = new List<AbsenceKeywordRedundancy>();
        foreach (var row in definition.Absences.Rows)
        {
            foreach (var command in definition.ScheduleCommands.CommandsOn(row.AgentId, row.Date))
            {
                result.Add(new AbsenceKeywordRedundancy(
                    Employee: row.AgentId,
                    Date: row.Date,
                    Keyword: command.Keyword,
                    BothBlockEveryShift: command.Keyword == ScheduleCommandKeyword.Free));
            }
        }

        return result
            .OrderBy(r => definition.ListRankOf(r.Employee))
            .ThenBy(r => r.Date)
            .ThenBy(r => r.Keyword)
            .ToList();
    }

    /// <summary>
    /// Audits the free-day rule instead of restating it. It walks the same package pairs and the same
    /// period edges the package metric walks and asks whether any day it would report as free is an
    /// absence day. The answer is false while <see cref="FreeDaysBetween"/> and
    /// <see cref="FreeEdgeDays"/> are the only sources of free days — which is the point: should a
    /// later change count free days somewhere else, this turns red instead of the report turning
    /// quietly wrong.
    /// </summary>
    /// <param name="packagesByEmployee">Work packages per employee</param>
    /// <param name="definition">Scenario carrying the absence rows</param>
    private static bool AnyAbsenceDayCountedAsFree(
        IReadOnlyDictionary<string, IReadOnlyList<WorkPackage>> packagesByEmployee,
        AutofillScenarioDefinition definition)
    {
        foreach (var employee in definition.EmployeesInListOrder)
        {
            var packages = packagesByEmployee.TryGetValue(employee, out var found) ? found : [];
            if (packages.Count == 0)
            {
                if (CountsAnyAbsence(definition.PeriodFrom, definition.PeriodUntil, employee, definition))
                {
                    return true;
                }

                continue;
            }

            for (var i = 0; i + 1 < packages.Count; i++)
            {
                if (FreeDaysBetween(packages[i].EndDate, packages[i + 1].StartDate, employee, definition)
                    .Any(day => definition.Absences.Covers(employee, day)))
                {
                    return true;
                }
            }

            if (packages[0].StartDate > definition.PeriodFrom
                && CountsAnyAbsence(definition.PeriodFrom, packages[0].StartDate.AddDays(-1), employee, definition))
            {
                return true;
            }

            if (packages[^1].EndDate < definition.PeriodUntil
                && CountsAnyAbsence(packages[^1].EndDate.AddDays(1), definition.PeriodUntil, employee, definition))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CountsAnyAbsence(
        DateOnly from, DateOnly until, string employee, AutofillScenarioDefinition definition)
    {
        var absentDays = 0;
        for (var date = from; date <= until; date = date.AddDays(1))
        {
            if (definition.Absences.Covers(employee, date))
            {
                absentDays++;
            }
        }

        var reported = FreeEdgeDays(from, until, employee, definition);
        return reported + absentDays != until.DayNumber - from.DayNumber + 1;
    }

    /// <summary>
    /// Carried-in packages an absence stopped at the month seam. They need their own pass: a package
    /// whose last day lies before the period is not part of the package list at all — it belongs to
    /// the previous month's plan — so the ordinary cut detection cannot see it, while its unserved
    /// days are exactly what the absence took away.
    /// </summary>
    /// <param name="packagesByEmployee">Work packages per employee</param>
    /// <param name="definition">Scenario carrying carry-ins and absences</param>
    private static IEnumerable<AbsenceCut> BuildCarryInCuts(
        IReadOnlyDictionary<string, IReadOnlyList<WorkPackage>> packagesByEmployee,
        AutofillScenarioDefinition definition)
    {
        foreach (var carryIn in definition.OpenCarryIns)
        {
            if (!definition.Absences.Covers(carryIn.AgentId, definition.PeriodFrom))
            {
                continue;
            }

            var packages = packagesByEmployee.TryGetValue(carryIn.AgentId, out var found) ? found : [];
            if (packages.Any(p => p.StartDate <= carryIn.PackageEndInclusive))
            {
                continue;
            }

            yield return new AbsenceCut(
                Employee: carryIn.AgentId,
                PackageStart: carryIn.PackageStart,
                PackageEnd: carryIn.PackageEndInclusive,
                LengthDays: carryIn.ServedDays,
                ShiftType: AutofillShiftCatalog.ShiftClassOf(carryIn.Kind),
                CutAt: definition.PeriodFrom,
                MissingDays: carryIn.MissingDays,
                Forced: true,
                Cause: AbsenceCause);
        }
    }

    private static (int Slots, int Required) MeasureAbsenceWindows(AutofillScenarioDefinition definition)
    {
        var windowDays = definition.Absences.Rows
            .Select(r => r.Date)
            .Distinct()
            .OrderBy(d => d)
            .ToList();
        if (windowDays.Count == 0)
        {
            return (0, 0);
        }

        var slots = 0;
        var required = 0;
        foreach (var day in windowDays)
        {
            slots += definition.EmployeesInListOrder.Count(e => !definition.Absences.Covers(e, day));
            required += definition.SlotsPerDay;
        }

        return (slots, required);
    }

    private static int WorkableDaysInRun(int runLength)
    {
        if (runLength <= 0)
        {
            return 0;
        }

        var cycles = runLength / RhythmCycleDays;
        var remainder = runLength % RhythmCycleDays;
        return (cycles * AutofillSpecConstants.MaxWorkDays)
            + Math.Min(remainder, AutofillSpecConstants.MaxWorkDays);
    }

    private static AutofillShiftKind ShiftClassOf(WorkPackage package)
        => AutofillShiftCatalog.ShiftClassOf(package.ShiftType);

    private static AutofillShiftKind ForwardSuccessorOf(AutofillShiftKind kind) => kind switch
    {
        AutofillShiftKind.Early => AutofillShiftKind.Late,
        AutofillShiftKind.Late => AutofillShiftKind.Night,
        _ => AutofillShiftKind.Early,
    };

    private static IReadOnlyList<PlannedShift> ShiftsOf(
        string employee, IReadOnlyDictionary<string, List<PlannedShift>> shiftsByEmployee)
        => shiftsByEmployee.TryGetValue(employee, out var shifts) ? shifts : [];

    private static string DescribeReasons(IReadOnlyList<AutofillBreakBlocker> rows)
    {
        var reasons = rows
            .Select(r => r.Reason)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        return reasons.Count == 0 ? NoReason : string.Join(ReasonSeparator, reasons);
    }
}
