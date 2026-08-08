// Copyright (c) Heribert Gasparoli Private. All rights reserved.

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
/// consecutive calendar days on which the employee holds at least one shift. A FREE BLOCK is a
/// maximal run of shift-free days BETWEEN two packages; days at the two ends of the period are
/// reported separately because nothing bounds them on the outside. A TRANSITION is the shift-kind
/// change from one package to the next of the same employee, forward meaning early to late to night
/// to early. The DATE OF A NIGHT SHIFT is the day it starts on, so a night on day d followed by an
/// early on day d+1 is the night-to-early violation.
/// </para>
/// </summary>
public static class AutofillPlanAnalyzer
{
    private const int ShiftKindCount = 3;
    private const string LengthHistogramOverflowBucket = "7+";
    private const int LengthHistogramLastNamedBucket = 6;

    private const string ForcedNote =
        "rotation.transitions[].forced is always false: a finished plan does not record whether the forward "
        + "successor shift was still available when the package started, so the flag cannot be derived. "
        + "Backward or skipping transitions must be judged by hand.";

    private const string PlannedHoursNote =
        "hours.plannedHours counts token hours without surcharges; every surcharge rate of the fixture is zero, "
        + "so planned hours equal shift hours exactly.";

    private const string PackageScopeNote =
        "packages cover in-period days only. Days of a carried-in package that lie in the previous month are "
        + "reported by the carry-in metrics, not by the package histogram.";

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
        var notes = new List<string> { ForcedNote, PlannedHoursNote, PackageScopeNote };

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
        var packagesByEmployee = byEmployee.ToDictionary(
            p => p.Key,
            p => BuildPackages(p.Key, p.Value, definition),
            StringComparer.Ordinal);

        return new AutofillMetrics(
            Scenario: scenarioName,
            TestName: testName,
            RunLabel: runLabel,
            PeriodFrom: definition.PeriodFrom,
            PeriodUntil: definition.PeriodUntil,
            Coverage: BuildCoverage(scenario, definition),
            Legality: BuildLegality(byEmployee, definition),
            Packages: BuildPackageMetrics(packagesByEmployee, definition),
            Rotation: BuildRotation(packagesByEmployee, definition),
            Hours: BuildHours(byEmployee, definition),
            Fairness: BuildFairness(byEmployee, definition),
            CarryIn: BuildCarryIn(byEmployee, definition),
            Determinism: new DeterminismMetrics(RunsIdentical: false, FirstDifference: "not compared"),
            Fitness: new EngineFitness(
                scenario.FitnessStage0,
                scenario.FitnessStage1,
                scenario.FitnessStage2,
                scenario.FitnessStage3,
                scenario.FitnessStage4,
                scenario.Fitness),
            Notes: notes);
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
                IsCarryIn: false));
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

    private static CoverageMetrics BuildCoverage(CoreScenario scenario, AutofillScenarioDefinition definition)
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

        foreach (var shift in context.Shifts)
        {
            var date = DateOnly.ParseExact(shift.Date, AutofillSpecConstants.IsoDateFormat);
            totalRequired += shift.RequiredAssignments;
            assignmentsPerSlot.TryGetValue((shift.Id, date), out var assigned);

            filled += Math.Min(assigned, shift.RequiredAssignments);
            if (assigned > shift.RequiredAssignments)
            {
                oversupplied++;
            }

            if (assigned < shift.RequiredAssignments)
            {
                unfilled.Add(new UnfilledShift(
                    date,
                    AutofillShiftCatalog.FromShiftTypeIndex(
                        ShiftTypeInference.FromSpanString(shift.StartTime, shift.EndTime))));
            }
        }

        var doubleBookings = new List<DoubleBooking>();
        foreach (var employee in definition.EmployeesInListOrder)
        {
            var perDay = scenario.Tokens
                .Where(t => string.Equals(t.AgentId, employee, StringComparison.Ordinal))
                .Where(t => t.Date >= definition.PeriodFrom && t.Date <= definition.PeriodUntil)
                .GroupBy(t => t.Date)
                .Where(g => g.Count() > 1)
                .OrderBy(g => g.Key);

            foreach (var day in perDay)
            {
                doubleBookings.Add(new DoubleBooking(
                    employee,
                    day.Key,
                    day.OrderBy(t => t.StartAt)
                        .Select(t => AutofillShiftCatalog.NameOf(AutofillShiftCatalog.FromShiftTypeIndex(t.ShiftTypeIndex)))
                        .ToList()));
            }
        }

        return new CoverageMetrics(
            TotalRequiredShifts: totalRequired,
            FilledShifts: filled,
            UnfilledShifts: unfilled.OrderBy(u => u.Date).ThenBy(u => u.ShiftType).ToList(),
            DoubleBookings: doubleBookings,
            OversuppliedSlots: oversupplied);
    }

    private static LegalityMetrics BuildLegality(
        IReadOnlyDictionary<string, List<PlannedShift>> byEmployee, AutofillScenarioDefinition definition)
    {
        var restViolations = new List<RestTimeViolation>();
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

        return new LegalityMetrics(restViolations, nightToEarly);
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
                    employee, date, carryIn.Kind, startAt, endAt, AutofillSpecConstants.ShiftHours, IsCarryIn: true));
            }
        }

        if (byEmployee.TryGetValue(employee, out var planned))
        {
            shifts.AddRange(planned);
        }

        shifts.Sort(ComparePlannedShifts);
        return shifts;
    }

    private static IReadOnlyList<WorkPackage> BuildPackages(
        string employee, IReadOnlyList<PlannedShift> shifts, AutofillScenarioDefinition definition)
    {
        var packages = new List<WorkPackage>();
        var byDay = shifts
            .GroupBy(s => s.Date)
            .OrderBy(g => g.Key)
            .Select(g => (Date: g.Key, Kinds: g.OrderBy(s => s.StartAt).Select(s => s.Kind).ToList()))
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
            var kinds = days.SelectMany(d => d.Kinds).Distinct().ToList();
            packages.Add(new WorkPackage(
                Employee: employee,
                StartDate: days[0].Date,
                EndDate: days[^1].Date,
                LengthDays: days.Count,
                ShiftType: days[0].Kinds[0],
                MixedTypes: kinds.Count > 1));
            index++;
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
                packages[0].StartDate.DayNumber - definition.PeriodFrom.DayNumber,
                definition.PeriodUntil.DayNumber - packages[^1].EndDate.DayNumber));
        }

        return new PackageMetrics(
            Items: all,
            LengthHistogram: BuildLengthHistogram(all),
            FreeBlockHistogram: freeBlocks,
            IdealShare: all.Count == 0 ? 0 : (double)idealCount / all.Count,
            MixedTypeCount: all.Count(p => p.MixedTypes),
            FreeEdges: edges);
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
        foreach (var employee in definition.EmployeesInListOrder)
        {
            var packages = packagesByEmployee.TryGetValue(employee, out var found) ? found : [];
            for (var i = 0; i + 1 < packages.Count; i++)
            {
                var from = packages[i].ShiftType;
                var to = packages[i + 1].ShiftType;
                transitions.Add(new RotationTransition(
                    employee,
                    from,
                    to,
                    Forward: (((int)from + 1) % ShiftKindCount) == (int)to,
                    Forced: false));
            }
        }

        var forward = transitions.Count(t => t.Forward);
        return new RotationMetrics(
            Transitions: transitions,
            ForwardRate: transitions.Count == 0 ? 0 : (double)forward / transitions.Count,
            BackwardOrSkipCount: transitions.Count - forward);
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

        return new HoursMetrics(perEmployee, byRank, violations);
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

        return new FairnessMetrics(
            ShiftTypeCountPerEmployee: counts,
            SpreadPerType: SpreadOf(counts),
            GiniPerType: new ShiftTypeRatioTriple(
                Early: Gini(counts.Select(c => c.Early).ToList()),
                Late: Gini(counts.Select(c => c.Late).ToList()),
                Night: Gini(counts.Select(c => c.Night).ToList())));
    }

    private static IReadOnlyList<CarryInRespect> BuildCarryIn(
        IReadOnlyDictionary<string, List<PlannedShift>> byEmployee, AutofillScenarioDefinition definition)
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

            results.Add(new CarryInRespect(
                Employee: carryIn.AgentId,
                ExpectedShiftType: carryIn.ExpectedFirstShiftKind,
                ActualFirstShiftType: first,
                ExpectedRemainingDays: carryIn.ExpectedRemainingDays,
                ActualRemainingDays: actualRemaining,
                Ok: first == carryIn.ExpectedFirstShiftKind && actualRemaining == carryIn.ExpectedRemainingDays));
        }

        return results;
    }
}
