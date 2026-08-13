// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis;

/// <summary>
/// Turns one finished plan into the specification's metric object. Scenario-independent: it is given
/// a plan and the scenario definition that produced it and knows nothing else, so the same analyzer
/// serves the clean start, the calibration variant, the carry-in scenario and the auction seed plan.
/// <para>
/// Definitions it applies, all taken from the specification. A PACKAGE is a maximal run of
/// consecutive calendar days on which the employee holds at least one shift, built over the carry-in
/// month and the period together: a run from 27 February to 3 March is ONE package of length 5 whose
/// kind and mixed-kind flag come from all five days. A FREE BLOCK is a maximal run of shift-free days
/// BETWEEN two packages; days at the two ends of the period are reported separately because nothing
/// bounds them on the outside. A TRANSITION is the shift-kind change from one package to the next of
/// the same employee, forward meaning early to late to night to early. The DATE OF A NIGHT SHIFT is
/// the day it starts on, so a night on day d followed by an early on day d+1 is the night-to-early
/// violation.
/// </para>
/// <para>
/// Which measure sees which days — the rule that keeps the assertion meanings apart. COVERAGE, HOURS
/// and FAIRNESS count in-period shifts only, because they answer what the engine planned for THIS
/// period; letting the fixed previous month leak in would silently move the coverage total and the
/// top-down hour references the assertions compare against. PACKAGES, ROTATION and the CARRY-IN
/// package length work on the whole cross-boundary package, because a package is a work rhythm that
/// does not stop at a month boundary. LEGALITY has always used both months, since rest time is
/// measured across the seam.
/// </para>
/// <para>
/// A DOUBLE BOOKING, since the owner corrected rule 1 on 2026-08-08, is no longer "more than one
/// shift on one day". Two different shifts on the same day that do not overlap are allowed, and rule
/// 11 asks for them when the day would otherwise leave the employee short of hours; the measure
/// therefore reports only the same shift assigned to one employee twice and two shifts of one
/// employee whose times overlap. Overlap is tested strictly, one shift starting BEFORE the other
/// ends, so the night 23:00-07:00 followed by the early 07:00 of the next day touches without
/// overlapping — that seam is a rest-time case and legality already measures it. The correction is
/// not a pure narrowing: it drops the same-day pairs and adds pairs that overlap across midnight.
/// With the suite's three spans of eight hours each, a cross-midnight overlap cannot occur, so on
/// scenarios 1, 1b and 2 the corrected measure reports exactly what the old one did — nothing.
/// </para>
/// <para>
/// Two border cases, decided explicitly. (1) A package that ended before the period starts — the
/// previous month closed it on 28 February — is NOT listed: it belongs to the previous month's plan,
/// which this run did not produce. Only packages holding at least one in-period day enter the item
/// list, the length histogram, mixedTypeCount and idealShare. (2) A free block consequently needs a
/// listed package on BOTH sides. Free days between a closed previous-month package and the first
/// listed package stay the leading free edge, exactly as in a scenario without carry-in. The price of
/// that rule is stated openly: a clean 5-work/2-free rhythm that spans the seam — five February days
/// followed by two free March days — cannot raise idealShare, because its work package is not listed.
/// The rule is chosen anyway because every package metric then derives from one single package list,
/// and a scenario without carry-in stays measurably unchanged.
/// </para>
/// <para>
/// ELIGIBILITY-DERIVED measures — pools, keyword violations, forced rotation reasons, forced
/// shortenings and the night cohort — exist only when the scenario carries a non-empty fixture ban
/// list; the measurement decisions behind them live in <see cref="EligibilityAnalyzer"/>. A scenario
/// without one keeps every one of those fields empty and measures what it measured before the
/// extension, because an empty ban list is the engine's own definition of "no eligibility
/// knowledge". The assignment-level diff of two runs is the one measure that takes two plans; it is
/// exposed separately as <see cref="DiffAssignments"/>.
/// </para>
/// </summary>
public static class AutofillPlanAnalyzer
{
    private const int ShiftKindCount = 3;
    private const string LengthHistogramOverflowBucket = "7+";
    private const int LengthHistogramLastNamedBucket = 6;
    private const string AssignmentTimeFormat = "yyyy-MM-dd HH:mm";
    private const string MultipleAssigneesSeparator = "+";

    private const string ForcedNote =
        "rotation.transitions[].forced is true only where the fixture ban list proves the forward successor "
        + "kind was closed to the employee on every in-period day of the following package (reason "
        + "keywordIneligible). No other cause is derivable from a finished plan, so every remaining deviation "
        + "carries reason unexplained with forced=false and must be judged by hand; in a scenario without a "
        + "ban list that is every deviation.";

    private const string EligibilityScanNote =
        "keyword.violations comes from an independent scan of every planned and carried-in shift against the "
        + "fixture ban list, never from the engine's ConstraintViolation list: the engine skips locked "
        + "carry-in assignments there, which would exempt exactly the stock the scan must cover.";

    private const string PlannedHoursNote =
        "hours.plannedHours counts token hours without surcharges; every surcharge rate of the fixture is zero, "
        + "so planned hours equal shift hours exactly.";

    private const string PackageScopeNote =
        "packages, rotation and carryIn[].actualPackageDays are measured on the whole package across the month "
        + "boundary: a run that starts in the carry-in month and reaches into the period is one package with one "
        + "length and one kind, so it may legitimately exceed the five-day ideal. A package that was already closed "
        + "before the period is not listed, and a free block needs a listed package on both sides — the free days "
        + "before the first listed package remain the leading freeEdge. coverage, hours and fairness keep counting "
        + "in-period shifts only.";

    /// <summary>
    /// Measures a plan.
    /// </summary>
    /// <param name="scenario">Plan produced by the engine</param>
    /// <param name="definition">Scenario that produced it; supplies list order, targets and carry-in facts</param>
    /// <param name="scenarioName">Scenario name for the artifact folder</param>
    /// <param name="testName">Test name for the artifact file</param>
    /// <param name="runLabel">Label of this run, for example run1, run2 or seed</param>
    public static AutofillMetrics Analyze(
        CoreScenario scenario,
        AutofillScenarioDefinition definition,
        string scenarioName,
        string testName,
        string runLabel)
    {
        EligibilityAnalyzer.EnsureUsable(definition);

        var notes = new List<string> { ForcedNote, PlannedHoursNote, PackageScopeNote };
        if (EligibilityAnalyzer.IsActive(definition))
        {
            notes.Add(EligibilityScanNote);
        }

        var outsidePeriod = TokensOutsidePeriod(scenario, definition);
        if (outsidePeriod.Count > 0)
        {
            notes.Add(
                $"{outsidePeriod.Count} token(s) carry a date outside [{definition.PeriodFrom:yyyy-MM-dd}, "
                + $"{definition.PeriodUntil:yyyy-MM-dd}]. The engine must never plan outside the period.");
        }

        if (definition.CarryInHoursCountedAsCurrentHours)
        {
            notes.Add(
                "Carry-in hours were handed to the engine as CoreAgent.CurrentHours, so the fitness already "
                + "counts them against the guaranteed hours and plans fewer in-period hours accordingly.");
        }

        var byEmployee = BuildEmployeePlans(scenario, definition);
        var packagesByEmployee = definition.EmployeesInListOrder.ToDictionary(
            employee => employee,
            employee => BuildPackages(employee, WithCarryIn(employee, byEmployee, definition), definition),
            StringComparer.Ordinal);

        var coverage = BuildCoverage(scenario, definition, byEmployee);

        return new AutofillMetrics(
            Scenario: scenarioName,
            TestName: testName,
            RunLabel: runLabel,
            PeriodFrom: definition.PeriodFrom,
            PeriodUntil: definition.PeriodUntil,
            Coverage: coverage,
            Legality: BuildLegality(byEmployee, definition),
            Packages: BuildPackageMetrics(packagesByEmployee, definition) with
            {
                ForcedExtraFreeDays = BuildForcedExtraFreeDays(byEmployee, coverage, definition),
            },
            Rotation: BuildRotation(packagesByEmployee, definition),
            Hours: BuildHours(byEmployee, definition),
            Fairness: BuildFairness(byEmployee, definition),
            Eligibility: EligibilityAnalyzer.BuildEligibility(definition),
            Keyword: EligibilityAnalyzer.BuildKeyword(scenario, definition),
            CarryIn: BuildCarryIn(byEmployee, packagesByEmployee, definition),
            Determinism: new DeterminismMetrics(RunsIdentical: false, FirstDifference: "not compared"),
            Fitness: new EngineFitness(
                scenario.FitnessStage0,
                scenario.FitnessStage1,
                scenario.FitnessStage2,
                scenario.FitnessStage3,
                scenario.FitnessStage4,
                scenario.Fitness),
            Notes: notes)
        {
            Orders = BuildOrders(byEmployee, definition),
            CarryInThreeDimensional = BuildCarryInThreeDimensional(byEmployee, definition),
        };
    }

    /// <summary>
    /// Flattens a plan into per-employee shift lists, sorted by day and start time. Shared with the
    /// matrix renderer so both draw the same picture.
    /// </summary>
    /// <param name="scenario">Plan produced by the engine</param>
    /// <param name="definition">Scenario that produced it</param>
    /// <param name="includeCarryIn">True to prepend the fixed shifts of the previous month</param>
    public static IReadOnlyDictionary<string, IReadOnlyList<PlannedShift>> BuildPlannedShifts(
        CoreScenario scenario, AutofillScenarioDefinition definition, bool includeCarryIn)
    {
        var planned = BuildEmployeePlans(scenario, definition);
        var result = new Dictionary<string, IReadOnlyList<PlannedShift>>(StringComparer.Ordinal);
        foreach (var employee in definition.EmployeesInListOrder)
        {
            result[employee] = includeCarryIn
                ? WithCarryIn(employee, planned, definition)
                : planned[employee];
        }

        return result;
    }

    /// <summary>
    /// Tokens the engine placed outside the planning period. Rule "no back-dating" expects this to be
    /// empty; the carry-in month is input, never output.
    /// </summary>
    /// <param name="scenario">Plan produced by the engine</param>
    /// <param name="definition">Scenario that produced it</param>
    public static IReadOnlyList<CoreToken> TokensOutsidePeriod(
        CoreScenario scenario, AutofillScenarioDefinition definition)
        => scenario.Tokens
            .Where(t => t.Date < definition.PeriodFrom || t.Date > definition.PeriodUntil)
            .OrderBy(t => t.AgentId, StringComparer.Ordinal)
            .ThenBy(t => t.Date)
            .ToList();

    /// <summary>
    /// Highest minus lowest count per shift kind over the given employees. Exposed separately so an
    /// assertion can restrict the spread to a subset of the list, for instance the top ranks only.
    /// </summary>
    /// <param name="counts">Per-employee shift counts to compare</param>
    public static ShiftTypeCountTriple SpreadOf(IEnumerable<EmployeeShiftTypeCounts> counts)
    {
        var materialised = counts.ToList();
        if (materialised.Count == 0)
        {
            return new ShiftTypeCountTriple(0, 0, 0);
        }

        return new ShiftTypeCountTriple(
            Early: materialised.Max(c => c.Early) - materialised.Min(c => c.Early),
            Late: materialised.Max(c => c.Late) - materialised.Min(c => c.Late),
            Night: materialised.Max(c => c.Night) - materialised.Min(c => c.Night));
    }

    /// <summary>
    /// Share of packages that are at most <paramref name="maxLength"/> days long. Returns 0 for a plan
    /// without packages, so an empty plan never looks like a perfect one.
    /// </summary>
    /// <param name="packages">Packages measured on the plan</param>
    /// <param name="maxLength">Longest length that still counts as short</param>
    public static double ShortPackageShare(PackageMetrics packages, int maxLength)
        => packages.Items.Count == 0
            ? 0
            : (double)packages.Items.Count(p => p.LengthDays <= maxLength) / packages.Items.Count;

    /// <summary>
    /// Number of packages longer than <paramref name="maxLength"/> days.
    /// </summary>
    /// <param name="packages">Packages measured on the plan</param>
    /// <param name="maxLength">Longest length the specification still allows</param>
    public static int PackagesLongerThan(PackageMetrics packages, int maxLength)
        => packages.Items.Count(p => p.LengthDays > maxLength);

    /// <summary>
    /// Gini coefficient of a set of counts: 0 means every employee holds the same number, 1 means one
    /// employee holds everything. Returns 0 for an empty set and for a set that sums to zero.
    /// </summary>
    /// <param name="values">Counts, one per employee</param>
    public static double Gini(IReadOnlyList<int> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        double total = values.Sum();
        if (total <= 0)
        {
            return 0;
        }

        double absoluteDifferences = 0;
        for (var i = 0; i < values.Count; i++)
        {
            for (var j = 0; j < values.Count; j++)
            {
                absoluteDifferences += Math.Abs(values[i] - values[j]);
            }
        }

        return absoluteDifferences / (2.0 * values.Count * total);
    }

    /// <summary>
    /// Compares two plans over the same demanded slots at assignment level — the control-group
    /// comparison of the suite, where the baseline is the unrestricted run and the treatment the run
    /// under a restriction, and every changed slot must later be attributable to that restriction.
    /// A slot counts as changed when the two plans staff it with different employees, including one
    /// side leaving it unfilled (null). Only in-period assignments are compared: the carry-in month
    /// is fixed input to both runs, not output of either. A slot oversupplied with several employees
    /// is compared as their names joined in ordinal order, so even a pathological plan diffs
    /// deterministically. The slot's kind is taken from the production time-span inference, exactly
    /// as coverage does, because the baseline scenario has no eligibility input to supply a kind map.
    /// </summary>
    /// <param name="baselinePlan">Plan of the control run</param>
    /// <param name="baselineDefinition">Scenario that produced the control run</param>
    /// <param name="treatmentPlan">Plan of the treated run</param>
    /// <param name="treatmentDefinition">Scenario that produced the treated run; must cover the same period</param>
    public static PlanAssignmentDiff DiffAssignments(
        CoreScenario baselinePlan,
        AutofillScenarioDefinition baselineDefinition,
        CoreScenario treatmentPlan,
        AutofillScenarioDefinition treatmentDefinition)
    {
        if (baselineDefinition.PeriodFrom != treatmentDefinition.PeriodFrom
            || baselineDefinition.PeriodUntil != treatmentDefinition.PeriodUntil)
        {
            throw new InvalidOperationException(
                $"The two scenarios cover different periods ({baselineDefinition.PeriodFrom:yyyy-MM-dd}.."
                + $"{baselineDefinition.PeriodUntil:yyyy-MM-dd} vs {treatmentDefinition.PeriodFrom:yyyy-MM-dd}.."
                + $"{treatmentDefinition.PeriodUntil:yyyy-MM-dd}); an assignment diff between them is meaningless.");
        }

        var slots = new Dictionary<(Guid ShiftId, DateOnly Date), AutofillShiftKind>();
        CollectSlots(baselineDefinition, slots);
        CollectSlots(treatmentDefinition, slots);

        var baselineAssignees = AssigneesPerSlot(baselinePlan, baselineDefinition);
        var treatmentAssignees = AssigneesPerSlot(treatmentPlan, treatmentDefinition);

        var changed = new List<ChangedAssignment>();
        foreach (var slot in slots
                     .OrderBy(s => s.Key.Date)
                     .ThenBy(s => s.Value)
                     .ThenBy(s => s.Key.ShiftId))
        {
            baselineAssignees.TryGetValue(slot.Key, out var employeeBaseline);
            treatmentAssignees.TryGetValue(slot.Key, out var employeeTreatment);
            if (!string.Equals(employeeBaseline, employeeTreatment, StringComparison.Ordinal))
            {
                changed.Add(new ChangedAssignment(slot.Key.Date, slot.Value, employeeBaseline, employeeTreatment));
            }
        }

        return new PlanAssignmentDiff(changed, changed.Count);
    }

    private static void CollectSlots(
        AutofillScenarioDefinition definition,
        Dictionary<(Guid ShiftId, DateOnly Date), AutofillShiftKind> slots)
    {
        foreach (var shift in definition.Context.Shifts)
        {
            var key = (Guid.Parse(shift.Id), DateOnly.ParseExact(shift.Date, AutofillSpecConstants.IsoDateFormat));
            slots[key] = AutofillShiftCatalog.FromShiftTypeIndex(
                ShiftTypeInference.FromSpanString(shift.StartTime, shift.EndTime));
        }
    }

    private static Dictionary<(Guid ShiftId, DateOnly Date), string> AssigneesPerSlot(
        CoreScenario plan, AutofillScenarioDefinition definition)
        => plan.Tokens
            .Where(t => t.Date >= definition.PeriodFrom && t.Date <= definition.PeriodUntil)
            .GroupBy(t => (t.ShiftRefId, t.Date))
            .ToDictionary(
                g => g.Key,
                g => string.Join(
                    MultipleAssigneesSeparator,
                    g.Select(t => t.AgentId).OrderBy(a => a, StringComparer.Ordinal)));

    private static Dictionary<string, List<PlannedShift>> BuildEmployeePlans(
        CoreScenario scenario, AutofillScenarioDefinition definition)
    {
        var byEmployee = new Dictionary<string, List<PlannedShift>>(StringComparer.Ordinal);
        foreach (var employee in definition.EmployeesInListOrder)
        {
            byEmployee[employee] = [];
        }

        foreach (var token in scenario.Tokens)
        {
            if (token.Date < definition.PeriodFrom || token.Date > definition.PeriodUntil)
            {
                continue;
            }

            if (!byEmployee.TryGetValue(token.AgentId, out var shifts))
            {
                shifts = [];
                byEmployee[token.AgentId] = shifts;
            }

            shifts.Add(new PlannedShift(
                token.AgentId,
                token.Date,
                AutofillShiftCatalog.FromShiftTypeIndex(token.ShiftTypeIndex),
                token.StartAt,
                token.EndAt,
                (double)token.TotalHours,
                IsCarryIn: false,
                ShiftRefId: token.ShiftRefId,
                Order: AutofillShiftCatalog.OrderOf(token.ShiftRefId)));
        }

        foreach (var shifts in byEmployee.Values)
        {
            shifts.Sort(ComparePlannedShifts);
        }

        return byEmployee;
    }

    private static int ComparePlannedShifts(PlannedShift left, PlannedShift right)
    {
        var byDate = left.Date.CompareTo(right.Date);
        return byDate != 0 ? byDate : left.StartAt.CompareTo(right.StartAt);
    }

    private static CoverageMetrics BuildCoverage(
        CoreScenario scenario,
        AutofillScenarioDefinition definition,
        IReadOnlyDictionary<string, List<PlannedShift>> byEmployee)
    {
        var context = definition.Context;
        var assignmentsPerSlot = new Dictionary<(string ShiftId, DateOnly Date), int>();
        foreach (var token in scenario.Tokens)
        {
            if (token.Date < definition.PeriodFrom || token.Date > definition.PeriodUntil)
            {
                continue;
            }

            var key = (token.ShiftRefId.ToString(), token.Date);
            assignmentsPerSlot.TryGetValue(key, out var current);
            assignmentsPerSlot[key] = current + 1;
        }

        var totalRequired = 0;
        var filled = 0;
        var oversupplied = 0;
        var unfilled = new List<UnfilledShift>();
        var requiredPerOrder = new SortedDictionary<int, int>();
        var filledPerOrder = new SortedDictionary<int, int>();

        foreach (var shift in context.Shifts)
        {
            var date = DateOnly.ParseExact(shift.Date, AutofillSpecConstants.IsoDateFormat);
            var order = AutofillShiftCatalog.OrderOf(Guid.Parse(shift.Id));
            totalRequired += shift.RequiredAssignments;
            assignmentsPerSlot.TryGetValue((shift.Id, date), out var assigned);

            var filledHere = Math.Min(assigned, shift.RequiredAssignments);
            filled += filledHere;

            requiredPerOrder.TryGetValue(order, out var requiredSoFar);
            requiredPerOrder[order] = requiredSoFar + shift.RequiredAssignments;
            filledPerOrder.TryGetValue(order, out var filledSoFar);
            filledPerOrder[order] = filledSoFar + filledHere;

            if (assigned > shift.RequiredAssignments)
            {
                oversupplied++;
            }

            if (assigned < shift.RequiredAssignments)
            {
                unfilled.Add(new UnfilledShift(
                    date,
                    AutofillShiftCatalog.FromShiftTypeIndex(
                        ShiftTypeInference.FromSpanString(shift.StartTime, shift.EndTime)),
                    order));
            }
        }

        var sortedUnfilled = unfilled.OrderBy(u => u.Date).ThenBy(u => u.Order).ThenBy(u => u.ShiftType).ToList();

        return new CoverageMetrics(
            TotalRequiredShifts: totalRequired,
            FilledShifts: filled,
            UnfilledShifts: sortedUnfilled,
            DoubleBookings: BuildDoubleBookings(scenario, definition),
            OversuppliedSlots: oversupplied)
        {
            PerOrder = definition.OrdersAreLabelled
                ? requiredPerOrder
                    .Select(entry => new OrderCoverage(
                        entry.Key,
                        entry.Value,
                        filledPerOrder.TryGetValue(entry.Key, out var value) ? value : 0,
                        sortedUnfilled.Where(u => u.Order == entry.Key).ToList()))
                    .ToList()
                : [],
            AssignmentsPerDay = BuildAssignmentsPerDay(scenario, definition),
            CrossOrderDoubleBookings = BuildCrossOrderDoubleBookings(byEmployee, definition),
        };
    }

    /// <summary>
    /// Assignments per calendar day over all orders, one entry for every day of the period including
    /// the empty ones. A night shift counts on the day it starts on, the same definition the whole
    /// suite uses, so a period of 31 days always yields 31 entries.
    /// </summary>
    /// <param name="scenario">Plan produced by the engine</param>
    /// <param name="definition">Scenario that produced it</param>
    private static IReadOnlyList<DayAssignmentCount> BuildAssignmentsPerDay(
        CoreScenario scenario, AutofillScenarioDefinition definition)
    {
        var counts = new SortedDictionary<DateOnly, int>();
        for (var date = definition.PeriodFrom; date <= definition.PeriodUntil; date = date.AddDays(1))
        {
            counts[date] = 0;
        }

        foreach (var token in scenario.Tokens)
        {
            if (token.Date < definition.PeriodFrom || token.Date > definition.PeriodUntil)
            {
                continue;
            }

            counts.TryGetValue(token.Date, out var current);
            counts[token.Date] = current + 1;
        }

        return counts.Select(entry => new DayAssignmentCount(entry.Key, entry.Value)).ToList();
    }

    /// <summary>
    /// Days on which one employee holds in-period shifts of more than one order. Independent of
    /// <see cref="BuildDoubleBookings"/>: two shifts of different orders that do not overlap are not a
    /// conflict under the corrected rule 1, but crossing an order boundary inside one day is exactly
    /// what scenario 4 wants counted.
    /// </summary>
    /// <param name="byEmployee">In-period shifts per employee</param>
    /// <param name="definition">Scenario that produced the plan</param>
    private static IReadOnlyList<CrossOrderDoubleBooking> BuildCrossOrderDoubleBookings(
        IReadOnlyDictionary<string, List<PlannedShift>> byEmployee, AutofillScenarioDefinition definition)
    {
        if (!definition.OrdersAreLabelled)
        {
            return [];
        }

        var found = new List<CrossOrderDoubleBooking>();
        foreach (var employee in definition.EmployeesInListOrder)
        {
            if (!byEmployee.TryGetValue(employee, out var shifts))
            {
                continue;
            }

            foreach (var day in shifts.GroupBy(s => s.Date).OrderBy(g => g.Key))
            {
                var onDay = day.OrderBy(s => s.StartAt).ToList();
                if (onDay.Select(s => s.Order).Distinct().Count() <= 1)
                {
                    continue;
                }

                found.Add(new CrossOrderDoubleBooking(
                    employee,
                    day.Key,
                    onDay.Select(s => s.Order).Distinct().OrderBy(o => o).ToList(),
                    onDay.Select(s => s.Kind).ToList()));
            }
        }

        return found;
    }

    /// <summary>
    /// The conflicting pairs of assignments of one plan: the same shift handed to one employee twice
    /// and two shifts of one employee whose times overlap. One entry per pair, dated on the earlier of
    /// the two shifts, so a pair reaching over midnight is reported once and not once per day. Like
    /// the rest of coverage this sees in-period shifts only; a carried-in night ends at 07:00 and can
    /// therefore not overlap an in-period shift.
    /// </summary>
    /// <param name="scenario">Plan produced by the engine</param>
    /// <param name="definition">Scenario that produced it; supplies list order and period bounds</param>
    private static IReadOnlyList<DoubleBooking> BuildDoubleBookings(
        CoreScenario scenario, AutofillScenarioDefinition definition)
    {
        var conflicts = new List<DoubleBooking>();
        foreach (var employee in definition.EmployeesInListOrder)
        {
            var assignments = scenario.Tokens
                .Where(t => string.Equals(t.AgentId, employee, StringComparison.Ordinal))
                .Where(t => t.Date >= definition.PeriodFrom && t.Date <= definition.PeriodUntil)
                .OrderBy(t => t.StartAt)
                .ThenBy(t => t.EndAt)
                .ThenBy(t => t.ShiftRefId)
                .ToList();

            for (var i = 0; i < assignments.Count; i++)
            {
                var earlier = assignments[i];
                for (var j = i + 1; j < assignments.Count; j++)
                {
                    var later = assignments[j];
                    if (later.StartAt >= earlier.EndAt)
                    {
                        break;
                    }

                    var sameShift = later.ShiftRefId == earlier.ShiftRefId && later.Date == earlier.Date;
                    conflicts.Add(new DoubleBooking(
                        employee,
                        earlier.Date,
                        [DescribeAssignment(earlier), DescribeAssignment(later)],
                        sameShift ? DoubleBooking.SameShiftTwiceReason : DoubleBooking.OverlappingTimesReason));
                }
            }
        }

        return conflicts;
    }

    /// <summary>One assignment as kind and absolute span, the form the A2 messages read.</summary>
    /// <param name="token">Assignment to describe</param>
    private static string DescribeAssignment(CoreToken token)
    {
        var kind = AutofillShiftCatalog.FromShiftTypeIndex(token.ShiftTypeIndex);
        var start = token.StartAt.ToString(AssignmentTimeFormat, CultureInfo.InvariantCulture);
        var end = token.EndAt.ToString(AssignmentTimeFormat, CultureInfo.InvariantCulture);
        return $"{kind} {start} to {end}";
    }

    private static LegalityMetrics BuildLegality(
        IReadOnlyDictionary<string, List<PlannedShift>> byEmployee, AutofillScenarioDefinition definition)
    {
        var restViolations = new List<RestTimeViolation>();
        var crossOrderRestViolations = new List<CrossOrderRestViolation>();
        var nightToEarly = new List<NightToEarlyViolation>();
        var minRestHours = AutofillSpecConstants.MinRestHours;

        foreach (var employee in definition.EmployeesInListOrder)
        {
            var shifts = WithCarryIn(employee, byEmployee, definition);

            for (var i = 1; i < shifts.Count; i++)
            {
                var previous = shifts[i - 1];
                var current = shifts[i];
                var gapHours = (current.StartAt - previous.EndAt).TotalHours;
                if (gapHours < minRestHours)
                {
                    restViolations.Add(new RestTimeViolation(
                        employee,
                        previous.Date,
                        current.Date,
                        previous.Kind,
                        current.Kind,
                        Math.Round(gapHours, 4)));

                    if (definition.OrdersAreLabelled && previous.Order != current.Order)
                    {
                        crossOrderRestViolations.Add(new CrossOrderRestViolation(
                            employee,
                            previous.Date,
                            current.Date,
                            previous.Order,
                            current.Order,
                            previous.Kind,
                            current.Kind,
                            Math.Round(gapHours, 4)));
                    }
                }
            }

            foreach (var night in shifts.Where(s => s.Kind == AutofillShiftKind.Night))
            {
                var nextDay = night.Date.AddDays(1);
                if (nextDay > definition.PeriodUntil)
                {
                    continue;
                }

                if (shifts.Any(s => s.Date == nextDay && s.Kind == AutofillShiftKind.Early))
                {
                    nightToEarly.Add(new NightToEarlyViolation(employee, night.Date));
                }
            }
        }

        return new LegalityMetrics(restViolations, nightToEarly)
        {
            RestViolationsCrossOrder = crossOrderRestViolations,
        };
    }

    private static List<PlannedShift> WithCarryIn(
        string employee,
        IReadOnlyDictionary<string, List<PlannedShift>> byEmployee,
        AutofillScenarioDefinition definition)
    {
        var shifts = new List<PlannedShift>();
        foreach (var carryIn in definition.CarryIns.Where(c => string.Equals(c.AgentId, employee, StringComparison.Ordinal)))
        {
            foreach (var date in carryIn.Days())
            {
                var (startAt, endAt) = AutofillShiftCatalog.SpanOf(carryIn.Kind, date);
                shifts.Add(new PlannedShift(
                    employee,
                    date,
                    carryIn.Kind,
                    startAt,
                    endAt,
                    AutofillSpecConstants.ShiftHours,
                    IsCarryIn: true,
                    ShiftRefId: AutofillShiftCatalog.ShiftIdOf(carryIn.OrderIndex, carryIn.Kind),
                    Order: carryIn.OrderIndex));
            }
        }

        if (byEmployee.TryGetValue(employee, out var planned))
        {
            shifts.AddRange(planned);
        }

        shifts.Sort(ComparePlannedShifts);
        return shifts;
    }

    /// <summary>
    /// Groups one employee's days into packages. The input carries the carry-in month as well, so a
    /// run reaching over the boundary becomes a single package; packages that end before the period
    /// starts are dropped again, because they belong to the previous month's plan.
    /// </summary>
    /// <param name="employee">Employee identifier</param>
    /// <param name="shifts">All shifts of that employee, carry-in days included, sorted by day</param>
    /// <param name="definition">Scenario that produced the plan; supplies the period bounds</param>
    private static IReadOnlyList<WorkPackage> BuildPackages(
        string employee, IReadOnlyList<PlannedShift> shifts, AutofillScenarioDefinition definition)
    {
        var packages = new List<WorkPackage>();
        var byDay = shifts
            .GroupBy(s => s.Date)
            .OrderBy(g => g.Key)
            .Select(g => (
                Date: g.Key,
                Kinds: g.OrderBy(s => s.StartAt).Select(s => s.Kind).ToList(),
                EarliestStart: g.Min(s => s.StartAt),
                LatestEnd: g.Max(s => s.EndAt)))
            .ToList();

        var index = 0;
        while (index < byDay.Count)
        {
            var start = index;
            while (index + 1 < byDay.Count && byDay[index + 1].Date == byDay[index].Date.AddDays(1))
            {
                index++;
            }

            var days = byDay.GetRange(start, index - start + 1);
            index++;

            if (days[^1].Date < definition.PeriodFrom)
            {
                continue;
            }

            var kinds = days.SelectMany(d => d.Kinds).Distinct().ToList();
            packages.Add(new WorkPackage(
                Employee: employee,
                StartDate: days[0].Date,
                EndDate: days[^1].Date,
                LengthDays: days.Count,
                ShiftType: days[0].Kinds[0],
                MixedTypes: kinds.Count > 1,
                FirstStartAt: days.Min(d => d.EarliestStart),
                LastEndAt: days.Max(d => d.LatestEnd)));
        }

        return packages;
    }

    private static PackageMetrics BuildPackageMetrics(
        IReadOnlyDictionary<string, IReadOnlyList<WorkPackage>> packagesByEmployee,
        AutofillScenarioDefinition definition)
    {
        var all = new List<WorkPackage>();
        var freeBlocks = new SortedDictionary<int, int>();
        var edges = new List<EmployeeFreeEdge>();
        var idealCount = 0;
        var periodLength = definition.PeriodUntil.DayNumber - definition.PeriodFrom.DayNumber + 1;

        foreach (var employee in definition.EmployeesInListOrder)
        {
            var packages = packagesByEmployee.TryGetValue(employee, out var found) ? found : [];
            all.AddRange(packages);

            if (packages.Count == 0)
            {
                edges.Add(new EmployeeFreeEdge(employee, periodLength, 0));
                continue;
            }

            for (var i = 0; i + 1 < packages.Count; i++)
            {
                var gap = packages[i + 1].StartDate.DayNumber - packages[i].EndDate.DayNumber - 1;
                if (gap <= 0)
                {
                    continue;
                }

                freeBlocks.TryGetValue(gap, out var current);
                freeBlocks[gap] = current + 1;

                if (packages[i].LengthDays == AutofillSpecConstants.MaxWorkDays
                    && gap == AutofillSpecConstants.MinRestDays)
                {
                    idealCount++;
                }
            }

            edges.Add(new EmployeeFreeEdge(
                employee,
                Math.Max(0, packages[0].StartDate.DayNumber - definition.PeriodFrom.DayNumber),
                definition.PeriodUntil.DayNumber - packages[^1].EndDate.DayNumber));
        }

        var (forcedShortenings, unexplainedShortenings) = EligibilityAnalyzer.BuildShortenings(all, definition);

        return new PackageMetrics(
            Items: all,
            LengthHistogram: BuildLengthHistogram(all),
            FreeBlockHistogram: freeBlocks,
            IdealShare: all.Count == 0 ? 0 : (double)idealCount / all.Count,
            MixedTypeCount: all.Count(p => p.MixedTypes),
            FreeEdges: edges,
            ForcedShortenings: forcedShortenings,
            UnexplainedShortenings: unexplainedShortenings);
    }

    private static IReadOnlyDictionary<string, int> BuildLengthHistogram(IReadOnlyList<WorkPackage> packages)
    {
        var histogram = new SortedDictionary<string, int>(StringComparer.Ordinal);
        for (var length = 1; length <= LengthHistogramLastNamedBucket; length++)
        {
            histogram[length.ToString()] = 0;
        }

        histogram[LengthHistogramOverflowBucket] = 0;

        foreach (var package in packages)
        {
            var bucket = package.LengthDays > LengthHistogramLastNamedBucket
                ? LengthHistogramOverflowBucket
                : package.LengthDays.ToString();
            histogram[bucket]++;
        }

        return histogram;
    }

    private static RotationMetrics BuildRotation(
        IReadOnlyDictionary<string, IReadOnlyList<WorkPackage>> packagesByEmployee,
        AutofillScenarioDefinition definition)
    {
        var transitions = new List<RotationTransition>();
        var restSeparated = 0;
        foreach (var employee in definition.EmployeesInListOrder)
        {
            var packages = packagesByEmployee.TryGetValue(employee, out var found) ? found : [];
            for (var i = 0; i + 1 < packages.Count; i++)
            {
                // Owner ruling 2026-08-12 (SPEC.md decision 12b): across at least the configured
                // rest days times 24 hours the next package is a free block restart, not a
                // rotation-bound transition.
                var restHours = (packages[i + 1].FirstStartAt - packages[i].LastEndAt).TotalHours;
                if (restHours >= AutofillSpecConstants.MinRestHoursBetweenPackages)
                {
                    restSeparated++;
                    continue;
                }

                var from = packages[i].ShiftType;
                var to = packages[i + 1].ShiftType;
                var isForward = (((int)from + 1) % ShiftKindCount) == (int)to;
                var reason = EligibilityAnalyzer.TransitionReasonOf(
                    employee, from, isForward, packages[i + 1], definition);
                transitions.Add(new RotationTransition(
                    employee,
                    from,
                    to,
                    Forward: isForward,
                    Forced: string.Equals(reason, RotationTransitionReason.KeywordIneligible, StringComparison.Ordinal),
                    Reason: reason));
            }
        }

        var forward = transitions.Count(t => t.Forward);
        return new RotationMetrics(
            Transitions: transitions,
            ForwardRate: transitions.Count == 0 ? 0 : (double)forward / transitions.Count,
            BackwardOrSkipCount: transitions.Count - forward,
            UnexplainedDeviations: transitions.Count(
                t => string.Equals(t.Reason, RotationTransitionReason.Unexplained, StringComparison.Ordinal)),
            RestSeparatedCount: restSeparated);
    }

    private static HoursMetrics BuildHours(
        IReadOnlyDictionary<string, List<PlannedShift>> byEmployee, AutofillScenarioDefinition definition)
    {
        var perEmployee = new List<EmployeeHours>();
        for (var i = 0; i < definition.EmployeesInListOrder.Count; i++)
        {
            var employee = definition.EmployeesInListOrder[i];
            var agent = definition.Context.Agents.First(a => string.Equals(a.Id, employee, StringComparison.Ordinal));
            var planned = byEmployee.TryGetValue(employee, out var shifts) ? shifts.Sum(s => s.Hours) : 0;
            perEmployee.Add(new EmployeeHours(
                Employee: employee,
                ListRank: i + 1,
                GuaranteedHours: agent.GuaranteedHours,
                PlannedHours: planned,
                FulfillmentPct: agent.GuaranteedHours <= 0 ? 0 : planned / agent.GuaranteedHours));
        }

        var byRank = perEmployee.Select(e => e.FulfillmentPct).ToList();
        var violations = new List<MonotonicityViolation>();
        for (var i = 1; i < byRank.Count; i++)
        {
            if (byRank[i] > byRank[i - 1] + AutofillSpecConstants.MonotonicityEpsilon)
            {
                violations.Add(new MonotonicityViolation(i + 1, byRank[i - 1], byRank[i]));
            }
        }

        return new HoursMetrics(perEmployee, byRank, violations)
        {
            ZeroHourEmployees = perEmployee.Where(e => e.PlannedHours <= 0).Select(e => e.Employee).ToList(),
        };
    }

    private static FairnessMetrics BuildFairness(
        IReadOnlyDictionary<string, List<PlannedShift>> byEmployee, AutofillScenarioDefinition definition)
    {
        var counts = new List<EmployeeShiftTypeCounts>();
        for (var i = 0; i < definition.EmployeesInListOrder.Count; i++)
        {
            var employee = definition.EmployeesInListOrder[i];
            var shifts = byEmployee.TryGetValue(employee, out var found) ? found : [];
            counts.Add(new EmployeeShiftTypeCounts(
                Employee: employee,
                ListRank: i + 1,
                Early: shifts.Count(s => s.Kind == AutofillShiftKind.Early),
                Late: shifts.Count(s => s.Kind == AutofillShiftKind.Late),
                Night: shifts.Count(s => s.Kind == AutofillShiftKind.Night)));
        }

        var (nightShares, cohortSpread) = EligibilityAnalyzer.BuildNightShares(counts, definition);

        return new FairnessMetrics(
            ShiftTypeCountPerEmployee: counts,
            SpreadPerType: SpreadOf(counts),
            GiniPerType: new ShiftTypeRatioTriple(
                Early: Gini(counts.Select(c => c.Early).ToList()),
                Late: Gini(counts.Select(c => c.Late).ToList()),
                Night: Gini(counts.Select(c => c.Night).ToList())),
            NightSharePerEligibleDay: nightShares,
            CohortSpreadNight: cohortSpread);
    }

    /// <summary>
    /// Measures each carried-in package against what the specification expects of it. The first kind
    /// and the remaining days stay in-period quantities — they ask what the engine did with THIS
    /// period — while <c>ActualPackageDays</c> reports the whole cross-boundary package that covers
    /// the seam, so the continuation can be read without re-deriving it from the package list.
    /// </summary>
    /// <param name="byEmployee">In-period shifts per employee</param>
    /// <param name="packagesByEmployee">Cross-boundary packages per employee</param>
    /// <param name="definition">Scenario that produced the plan</param>
    private static IReadOnlyList<CarryInRespect> BuildCarryIn(
        IReadOnlyDictionary<string, List<PlannedShift>> byEmployee,
        IReadOnlyDictionary<string, IReadOnlyList<WorkPackage>> packagesByEmployee,
        AutofillScenarioDefinition definition)
    {
        var results = new List<CarryInRespect>();
        foreach (var carryIn in definition.CarryIns)
        {
            var shifts = byEmployee.TryGetValue(carryIn.AgentId, out var found) ? found : [];
            var first = shifts.Count == 0 ? null : (AutofillShiftKind?)shifts[0].Kind;

            var actualRemaining = 0;
            var probe = definition.PeriodFrom;
            while (shifts.Any(s => s.Date == probe && s.Kind == carryIn.Kind))
            {
                actualRemaining++;
                probe = probe.AddDays(1);
            }

            var packages = packagesByEmployee.TryGetValue(carryIn.AgentId, out var owned) ? owned : [];
            var seamPackage = packages.FirstOrDefault(
                p => p.StartDate <= definition.PeriodFrom && p.EndDate >= definition.PeriodFrom);

            results.Add(new CarryInRespect(
                Employee: carryIn.AgentId,
                ExpectedShiftType: carryIn.ExpectedFirstShiftKind,
                ActualFirstShiftType: first,
                ExpectedRemainingDays: carryIn.ExpectedRemainingDays,
                ActualRemainingDays: actualRemaining,
                Ok: first == carryIn.ExpectedFirstShiftKind && actualRemaining == carryIn.ExpectedRemainingDays,
                ActualPackageDays: seamPackage?.LengthDays ?? 0));
        }

        return results;
    }

    /// <summary>
    /// The same continuation check with the order added as a third dimension. The remaining days are
    /// counted on the (order, kind) pair the package ran on, so continuing the right kind on the WRONG
    /// order counts as zero remaining days and not as a continuation. An entry whose expected order is
    /// null does not judge the order at all — the specification's rotation table for closed packages
    /// names a shift kind and no order.
    /// </summary>
    /// <param name="byEmployee">In-period shifts per employee</param>
    /// <param name="definition">Scenario that produced the plan</param>
    private static IReadOnlyList<CarryInOrderRespect> BuildCarryInThreeDimensional(
        IReadOnlyDictionary<string, List<PlannedShift>> byEmployee, AutofillScenarioDefinition definition)
    {
        if (!definition.OrdersAreLabelled)
        {
            return [];
        }

        var results = new List<CarryInOrderRespect>();
        foreach (var carryIn in definition.CarryIns)
        {
            var shifts = byEmployee.TryGetValue(carryIn.AgentId, out var found) ? found : [];
            var firstKind = shifts.Count == 0 ? null : (AutofillShiftKind?)shifts[0].Kind;
            var firstOrder = shifts.Count == 0 ? null : (int?)shifts[0].Order;

            var actualRemaining = 0;
            var probe = definition.PeriodFrom;
            while (shifts.Any(s => s.Date == probe && s.Kind == carryIn.Kind && s.Order == carryIn.OrderIndex))
            {
                actualRemaining++;
                probe = probe.AddDays(1);
            }

            var orderOk = carryIn.ExpectedFirstOrderIndex is null || firstOrder == carryIn.ExpectedFirstOrderIndex;
            results.Add(new CarryInOrderRespect(
                Employee: carryIn.AgentId,
                ExpectedOrder: carryIn.ExpectedFirstOrderIndex,
                ActualOrder: firstOrder,
                ExpectedShiftType: carryIn.ExpectedFirstShiftKind,
                ActualShiftType: firstKind,
                ExpectedRemainingDays: carryIn.ExpectedRemainingDays,
                ActualRemainingDays: actualRemaining,
                Ok: orderOk
                    && firstKind == carryIn.ExpectedFirstShiftKind
                    && actualRemaining == carryIn.ExpectedRemainingDays));
        }

        return results;
    }

    /// <summary>
    /// Rule 10 as numbers. Package purity is measured on the whole cross-boundary package, the same
    /// scope every other package measure uses, while the per-employee switch sequence and the order
    /// distribution count in-period shifts only, because they ask what THIS run planned.
    /// </summary>
    /// <param name="byEmployee">In-period shifts per employee</param>
    /// <param name="definition">Scenario that produced the plan</param>
    private static OrderMetrics BuildOrders(
        IReadOnlyDictionary<string, List<PlannedShift>> byEmployee, AutofillScenarioDefinition definition)
    {
        if (!definition.OrdersAreLabelled)
        {
            return OrderMetrics.Empty;
        }

        var switchesInPackage = new List<OrderSwitchInPackage>();
        var switchesPerEmployee = new List<OrderSwitchesPerEmployee>();
        var employeesPerOrder = new SortedDictionary<int, List<string>>();
        var rankWeightPerOrder = new SortedDictionary<int, (double WeightedRank, int Shifts)>();

        for (var rankIndex = 0; rankIndex < definition.EmployeesInListOrder.Count; rankIndex++)
        {
            var employee = definition.EmployeesInListOrder[rankIndex];
            var listRank = rankIndex + 1;

            foreach (var (packageStart, orders) in OrderRunsOf(
                         WithCarryIn(employee, byEmployee, definition), definition))
            {
                if (orders.Distinct().Count() > 1)
                {
                    switchesInPackage.Add(new OrderSwitchInPackage(employee, packageStart, orders));
                }
            }

            var inPeriod = byEmployee.TryGetValue(employee, out var found) ? found : [];
            var sequence = inPeriod.Select(s => s.Order).ToList();
            var switchCount = 0;
            for (var i = 1; i < sequence.Count; i++)
            {
                if (sequence[i] != sequence[i - 1])
                {
                    switchCount++;
                }
            }

            switchesPerEmployee.Add(new OrderSwitchesPerEmployee(employee, switchCount, sequence));

            foreach (var group in inPeriod.GroupBy(s => s.Order))
            {
                if (!employeesPerOrder.TryGetValue(group.Key, out var list))
                {
                    list = [];
                    employeesPerOrder[group.Key] = list;
                }

                list.Add(employee);

                rankWeightPerOrder.TryGetValue(group.Key, out var current);
                rankWeightPerOrder[group.Key] = (
                    current.WeightedRank + (listRank * (double)group.Count()),
                    current.Shifts + group.Count());
            }
        }

        var distribution = employeesPerOrder
            .Select(entry =>
            {
                var weight = rankWeightPerOrder[entry.Key];
                return new OrderEmployeeDistribution(
                    entry.Key,
                    entry.Value,
                    weight.Shifts == 0 ? 0 : weight.WeightedRank / weight.Shifts);
            })
            .ToList();

        return new OrderMetrics(switchesInPackage, switchesPerEmployee, distribution);
    }

    /// <summary>
    /// The orders one employee touches inside each of his packages, in day sequence. Uses the same
    /// package definition as <see cref="BuildPackages"/>, packages closed before the period included
    /// in the grouping and dropped afterwards, so a package here is the same package there.
    /// </summary>
    /// <param name="shifts">All shifts of one employee, carry-in days included, sorted by day</param>
    /// <param name="definition">Scenario that produced the plan; supplies the period bounds</param>
    private static IEnumerable<(DateOnly PackageStart, IReadOnlyList<int> Orders)> OrderRunsOf(
        IReadOnlyList<PlannedShift> shifts, AutofillScenarioDefinition definition)
    {
        var byDay = shifts
            .GroupBy(s => s.Date)
            .OrderBy(g => g.Key)
            .Select(g => (Date: g.Key, Orders: g.OrderBy(s => s.StartAt).Select(s => s.Order).ToList()))
            .ToList();

        var index = 0;
        while (index < byDay.Count)
        {
            var start = index;
            while (index + 1 < byDay.Count && byDay[index + 1].Date == byDay[index].Date.AddDays(1))
            {
                index++;
            }

            var days = byDay.GetRange(start, index - start + 1);
            index++;

            if (days[^1].Date < definition.PeriodFrom)
            {
                continue;
            }

            yield return (days[0].Date, days.SelectMany(d => d.Orders).ToList());
        }
    }

    /// <summary>
    /// The leading free days of every employee that the finished plan offered no way to fill. A day is
    /// unfillable for an employee when no slot of that day stayed open that the employee could have
    /// taken without breaking the rest time against his own neighbouring shifts. The plan records no
    /// such flag, so this is derived from the open slots — which is why the measure stops at the first
    /// package: beyond it, free days are the 5/2 rhythm and the derivation would report the whole plan.
    /// </summary>
    /// <param name="byEmployee">In-period shifts per employee</param>
    /// <param name="coverage">Coverage of the same plan; supplies the slots that stayed open</param>
    /// <param name="definition">Scenario that produced the plan</param>
    private static IReadOnlyList<ForcedFreeDayRun> BuildForcedExtraFreeDays(
        IReadOnlyDictionary<string, List<PlannedShift>> byEmployee,
        CoverageMetrics coverage,
        AutofillScenarioDefinition definition)
    {
        var runs = new List<ForcedFreeDayRun>();
        var openByDay = coverage.UnfilledShifts
            .GroupBy(u => u.Date)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var employee in definition.EmployeesInListOrder)
        {
            var all = WithCarryIn(employee, byEmployee, definition);
            var inPeriod = byEmployee.TryGetValue(employee, out var found) ? found : [];
            var firstWorkedDay = inPeriod.Count == 0 ? definition.PeriodUntil.AddDays(1) : inPeriod[0].Date;
            if (firstWorkedDay <= definition.PeriodFrom)
            {
                continue;
            }

            var lastFreeDay = firstWorkedDay.AddDays(-1);
            for (var day = definition.PeriodFrom; day <= lastFreeDay; day = day.AddDays(1))
            {
                if (openByDay.TryGetValue(day, out var openSlots)
                    && openSlots.Any(slot => CouldHaveTaken(all, slot)))
                {
                    lastFreeDay = day.AddDays(-1);
                    break;
                }
            }

            if (lastFreeDay < definition.PeriodFrom)
            {
                continue;
            }

            var openSlotInRun = false;
            for (var day = definition.PeriodFrom; day <= lastFreeDay; day = day.AddDays(1))
            {
                openSlotInRun |= openByDay.ContainsKey(day);
            }

            runs.Add(new ForcedFreeDayRun(
                employee,
                definition.PeriodFrom,
                lastFreeDay,
                openSlotInRun ? ForcedFreeDayRun.NoLegalOpenSlotCause : ForcedFreeDayRun.NoOpenSlotCause));
        }

        return runs;
    }

    /// <summary>
    /// Whether one employee could have taken one open slot without breaking the contractual rest time
    /// against any shift he actually holds. Deliberately the weakest honest test: it proves a day was
    /// fillable, never that it was not, so a run reported as forced is forced under a check that errs
    /// towards NOT reporting.
    /// </summary>
    /// <param name="shifts">Every shift of the employee, carry-in days included</param>
    /// <param name="slot">Open slot to try</param>
    private static bool CouldHaveTaken(IReadOnlyList<PlannedShift> shifts, UnfilledShift slot)
    {
        var (startAt, endAt) = AutofillShiftCatalog.SpanOf(slot.ShiftType, slot.Date);
        foreach (var shift in shifts)
        {
            if (startAt < shift.EndAt && shift.StartAt < endAt)
            {
                return false;
            }

            var gapHours = startAt >= shift.EndAt
                ? (startAt - shift.EndAt).TotalHours
                : (shift.StartAt - endAt).TotalHours;
            if (gapHours < AutofillSpecConstants.MinRestHours)
            {
                return false;
            }
        }

        return true;
    }
}
