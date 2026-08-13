// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Analysis;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Support;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario2;

/// <summary>
/// Assertions A1, A2, A3, A6, A9 to A14 of the carry-in family, shared by scenario 2 (the plain month
/// transition) and scenario 3 (the same fixture plus keyword restrictions, whose specification
/// inherits them unchanged onto its main run L1). Extracted verbatim from <see cref="Scenario2Tests"/>
/// on 2026-08-08.
/// <para>
/// A deriving fixture builds and runs its scenario once in its own <c>[OneTimeSetUp]</c> and exposes
/// the cached result through <see cref="Definition"/> and <see cref="Run"/>; both accessors are
/// expected to report an invalid fixture (for example via <c>Assert.Inconclusive</c>) instead of
/// returning a half-built scenario, which keeps a setup mistake from ever being read as a verdict
/// about the algorithm.
/// </para>
/// <para>
/// A4, A5, A7 and A8 were REMOVED here on 2026-08-12 (SPEC.md decision 11). They were permanently
/// red, and a red test guards nothing: an engine rewrite could have halved any of the four values
/// without a single test noticing. Their measurements are pinned instead by the <c>Baseline_</c>
/// guards of <see cref="AutofillBaselineTestBase"/> — mixedTypeCount for A4, shortPackageShare,
/// packagesOverIdealLength and idealShare for A5, forwardRate for A7, shiftKindSpread for A8 — and
/// their specification targets stay binding in tests/autofill/SPEC.md. Two readings are covered by no
/// pin and only survive as targets there: the package-length MODE of A5 and the rank-scoped spread of
/// A8, which the baseline guard measures over all employees instead. A6 and A13 remained but now
/// judge a pinned measurement rather than the specification value; each states both on itself. A12
/// asserts the specification value again since the hour-based rest reading of decision 12d.
/// </para>
/// <para>
/// A15 (recovery comparison run) is deliberately not implemented in any deriving fixture. Decision 5
/// in the header of tests/autofill/SPEC.md: recovery is a failure repair, not a month-transition
/// mechanism, and A15 is carried in phase D as NOT-EVALUABLE, category c.
/// </para>
/// </summary>
public abstract class Scenario2AssertionsBase : AutofillBaselineTestBase
{
    /// <summary>Separator between the individual problem lines of one assertion message.</summary>
    protected const string ProblemSeparator = " | ";

    private const string NumberFormat = "0.###";

    private const double HoursComparisonEpsilon = 1e-9;

    /// <summary>
    /// Pinned measurement 2026-08-12 (SPEC.md decision 11): the tolerance the top-down order of A6 is
    /// judged with. Rule 5 of the binding priority table demands that the fulfilment never rises going
    /// down the list, and that stays the specification target — but a plan is built out of whole
    /// shifts, so the hours of a rank can only ever move in steps of one daily working time. A rank
    /// that exceeds the LOWEST planned hours of all ranks above it by at most one such step is an
    /// artefact of that granularity, not an inversion of the order. The value is one shift of
    /// <see cref="AutofillSpecConstants.ShiftHours"/> hours, which is one daily working time of every
    /// employee of this fixture. On the decision-12 engine (2026-08-12 evening) scenario 2 reaches
    /// 176/168/160/144/96 h and does not use the tolerance; scenario 3 levels to 160/152/144/136/152 h
    /// and overrides <see cref="TopDownToleranceHours"/> with a pinned wider band. The STRICT pairwise
    /// reading survives untouched in
    /// <see cref="AutofillBaselineTestBase.Baseline_HourMonotonicityViolationsDidNotGrow"/>.
    /// </summary>
    private const double TopDownOrderToleranceHours = AutofillSpecConstants.ShiftHours;

    /// <summary>
    /// Tolerance the A6 order judgement runs against — one shift length by default, the band decision
    /// B5 established. Scenario 3 overrides it with a pinned wider band (SPEC.md decision 13) because
    /// the unescalatable rest levels its hours; the base value stays the specification reading.
    /// </summary>
    protected virtual double TopDownToleranceHours => TopDownOrderToleranceHours;

    /// <summary>
    /// Pinned measurement 2026-08-12 (SPEC.md decision 11): closed carry-in packages whose first
    /// package of the period does NOT rotate as A13 demands. The specification target is 0 — a package
    /// that ended in the previous month must not restart at the same kind — and stays binding in
    /// tests/autofill/SPEC.md. The one miss measured on 2026-08-12 (MA-5, Late again instead of
    /// Night) dissolved under the decision-12b measurement rework of 2026-08-13: MA-5 restarts 64
    /// hours after its closed package and owes no rotation, so the pin returned to the
    /// specification target as decision 6 demands. The suspected "largest open engine gap of the
    /// carry-in family" was a measurement artifact.
    /// </summary>
    private const int MaxBoundaryRotationMisses = 0;

    /// <summary>The scenario of the run the assertions read; guards its own fixture validity.</summary>
    protected abstract AutofillScenarioDefinition Definition { get; }

    /// <summary>The cached run of the fixture; guards its own fixture validity.</summary>
    protected abstract DeterministicRunResult Run { get; }

    /// <summary>Measurement of run 1 — the plan every assertion judges.</summary>
    protected AutofillMetrics Metrics => Run.Metrics;

    /// <summary>The no-regression floor reads the same cached measurement as the rule assertions.</summary>
    protected override AutofillMetrics BaselineMetrics => Metrics;

    [Test]
    public void A1_EveryShiftOfThePeriodIsStaffed()
    {
        var coverage = Metrics.Coverage;

        coverage.UnfilledShifts.ShouldBeEmpty(
            $"A1: all {coverage.TotalRequiredShifts.ToString(CultureInfo.InvariantCulture)} demanded shifts of "
            + $"{Definition.PeriodFrom:yyyy-MM-dd}..{Definition.PeriodUntil:yyyy-MM-dd} must be staffed, but "
            + $"{coverage.UnfilledShifts.Count.ToString(CultureInfo.InvariantCulture)} are empty (filled: "
            + $"{coverage.FilledShifts.ToString(CultureInfo.InvariantCulture)}). Empty slots: "
            + Scenario2Diagnostics.DescribeUnfilled(coverage.UnfilledShifts));
    }

    [Test]
    public void A2_NobodyIsBookedTwiceOnTheSameDay()
    {
        var coverage = Metrics.Coverage;

        coverage.DoubleBookings.ShouldBeEmpty(
            "A2: one employee may hold two shifts on the same calendar day as long as they are different shifts "
            + "that do not overlap; only the same shift assigned twice and two shifts overlapping in time are "
            + $"conflicts, but {coverage.DoubleBookings.Count.ToString(CultureInfo.InvariantCulture)} conflict(s) "
            + "were found: " + Scenario2Diagnostics.DescribeDoubleBookings(coverage.DoubleBookings));
    }

    [Test]
    public void A3_RestTimeAndNightToEarlyAreRespected()
    {
        var legality = Metrics.Legality;
        var problems = new List<string>();

        if (legality.NightToEarlyViolations.Count > 0)
        {
            problems.Add(
                $"{legality.NightToEarlyViolations.Count.ToString(CultureInfo.InvariantCulture)} night-to-early "
                + $"transition(s): {Scenario2Diagnostics.DescribeNightToEarly(legality.NightToEarlyViolations)}");
        }

        if (legality.RestViolations.Count > 0)
        {
            problems.Add(
                $"{legality.RestViolations.Count.ToString(CultureInfo.InvariantCulture)} shift pair(s) below the "
                + $"contractual rest time of {AutofillSpecConstants.MinRestHours.ToString(CultureInfo.InvariantCulture)} h: "
                + Scenario2Diagnostics.DescribeRestViolations(legality.RestViolations));
        }

        problems.ShouldBeEmpty(
            "A3: the plan must contain neither a rest-time violation nor a night shift followed by an early shift. "
            + "The carry-in days of February take part in this check, so the seam at 2026-03-01 is covered. "
            + Describe(problems));
    }

    /// <summary>
    /// A6, reduced to a pinned measurement on 2026-08-12 (SPEC.md decision 11). What was removed: the
    /// requirement that ranks 1 to <see cref="AutofillSpecConstants.TopRankUpperBound"/> each reach
    /// 95 % of the <see cref="AutofillSpecConstants.ReferenceHoursTopRanks"/> reference. That part was
    /// permanently red and is geometrically unreachable in this fixture — proof P5 of 2026-08-08 — so
    /// it stated a target, not a guard. What covers it now, and what does not: the SUM over the top
    /// ranks is pinned by <see cref="AutofillBaselineTestBase.Baseline_TopRankPlannedHoursDidNotFall"/>,
    /// so hours cannot drift down the list unnoticed. The PER-RANK floor is not pinned by anything — a
    /// sum of 616 h is also reached by 616/0/0/0 — and survives only as a target in
    /// tests/autofill/SPEC.md; the per-rank figures stay in the message below so a reader sees them.
    /// What remains asserted here is the top-down ORDER of rule 5, judged with the
    /// <see cref="TopDownOrderToleranceHours"/> tolerance band. The specification target itself is
    /// unchanged and stricter, and stays documented in tests/autofill/SPEC.md.
    /// </summary>
    [Test]
    public void A6_GuaranteedHoursAreServedTopDown()
    {
        var hours = Metrics.Hours;
        var threshold = AutofillSpecConstants.ReferenceHoursTopRanks * AutofillSpecConstants.FulfilmentThreshold;
        var problems = new List<string>();
        var lowestAbove = double.MaxValue;

        foreach (var employee in hours.PerEmployee.OrderBy(e => e.ListRank))
        {
            if (employee.PlannedHours > lowestAbove + TopDownToleranceHours + HoursComparisonEpsilon)
            {
                problems.Add(
                    $"rank {employee.ListRank.ToString(CultureInfo.InvariantCulture)} {employee.Employee} holds "
                    + $"{employee.PlannedHours.ToString("0.#", CultureInfo.InvariantCulture)} h, more than the "
                    + $"tolerated {TopDownToleranceHours.ToString("0.#", CultureInfo.InvariantCulture)} h above "
                    + $"the {lowestAbove.ToString("0.#", CultureInfo.InvariantCulture)} h of the weakest rank above "
                    + "it");
            }

            lowestAbove = Math.Min(lowestAbove, employee.PlannedHours);
        }

        problems.ShouldBeEmpty(
            "A6: pinned measurement 2026-08-12 — going down the list a rank may exceed the lowest planned hours of "
            + $"all ranks above it by at most {TopDownToleranceHours.ToString("0.#", CultureInfo.InvariantCulture)} h, "
            + "the step a plan of whole shifts moves in. The specification target is unchanged and stricter "
            + "(SPEC.md rule 5): the fulfilment never rises at all, and ranks 1 to "
            + $"{AutofillSpecConstants.TopRankUpperBound.ToString(CultureInfo.InvariantCulture)} each reach "
            + $"{threshold.ToString("0.#", CultureInfo.InvariantCulture)} h, that is "
            + $"{(AutofillSpecConstants.FulfilmentThreshold * 100).ToString("0.#", CultureInfo.InvariantCulture)} % of "
            + $"the reference {AutofillSpecConstants.ReferenceHoursTopRanks.ToString("0.#", CultureInfo.InvariantCulture)} h "
            + $"({AutofillSpecConstants.ReferenceShiftsTopRanks.ToString(CultureInfo.InvariantCulture)} shifts). That "
            + "reference part was removed from the assertion on 2026-08-12 because it is geometrically unreachable "
            + $"here; their SUM is pinned by {nameof(Baseline_TopRankPlannedHoursDidNotFall)}, the strict pairwise "
            + $"order by {nameof(Baseline_HourMonotonicityViolationsDidNotGrow)}, while the per-rank floor is "
            + "guarded by nothing and stays a target only. All ranks: "
            + $"{Scenario2Diagnostics.DescribeHours(hours.PerEmployee)}. The February hours are deliberately kept "
            + "out of CurrentHours so this stays comparable to scenario 1. " + Describe(problems));
    }

    [Test]
    public void A9_TwoRunsWithTheSameSeedProduceTheSamePlan()
    {
        Run.RunsIdentical.ShouldBeTrue(
            $"A9: two runs with seed {Definition.Config.RandomSeed.ToString(CultureInfo.InvariantCulture)}, "
            + "sequential evaluation and no wall-clock budget must produce the identical plan, but they differ at "
            + $"{Run.FirstDifference}");
    }

    [Test]
    public void A10_CarriedInPackagesKeepTheirShiftKind()
    {
        var openEmployees = Definition.OpenCarryIns.Select(c => c.AgentId).ToList();
        var measured = CarryInEntriesOf(openEmployees);
        var wrong = measured.Where(c => c.ActualFirstShiftType != c.ExpectedShiftType).ToList();

        wrong.ShouldBeEmpty(
            "A10: an employee still inside a previous-month package must start the period with the very shift kind "
            + $"of that package (MA-1 night, MA-2 late, MA-3 early), but "
            + $"{wrong.Count.ToString(CultureInfo.InvariantCulture)} of "
            + $"{measured.Count.ToString(CultureInfo.InvariantCulture)} do not: "
            + $"{Scenario2Diagnostics.DescribeCarryIn(wrong)}. Same measurement on the auction seed plan, before any "
            + $"evolution ran: {Scenario2Diagnostics.DescribeSeedCarryIn(Run.SeedMetrics, openEmployees)}. An "
            + "identical failure there places the defect in the seeding, not in the evolution.");
    }

    [Test]
    public void A11_CarriedInPackagesAreCompletedToFiveShifts()
    {
        var openEmployees = Definition.OpenCarryIns.Select(c => c.AgentId).ToList();
        var measured = CarryInEntriesOf(openEmployees);
        var wrong = measured.Where(c => c.ActualRemainingDays != c.ExpectedRemainingDays).ToList();

        wrong.ShouldBeEmpty(
            "A11: a started package must be completed to exactly "
            + $"{AutofillSpecConstants.MaxWorkDays.ToString(CultureInfo.InvariantCulture)} shifts instead of being "
            + $"started anew — MA-1 owes {Scenario2SpecValues.Ma1RemainingDays.ToString(CultureInfo.InvariantCulture)} "
            + $"more nights, MA-2 {Scenario2SpecValues.Ma2RemainingDays.ToString(CultureInfo.InvariantCulture)} more "
            + $"late shift, MA-3 {Scenario2SpecValues.Ma3RemainingDays.ToString(CultureInfo.InvariantCulture)} more "
            + $"early shifts. Wrong: {Scenario2Diagnostics.DescribeCarryIn(wrong)}. Same measurement on the auction "
            + $"seed plan: {Scenario2Diagnostics.DescribeSeedCarryIn(Run.SeedMetrics, openEmployees)}. An identical "
            + "failure there places the defect in the seeding, not in the evolution.");
    }

    /// <summary>
    /// Core of assertion A12, shared by both heirs; each heir declares its own [Test] wrapper. Since
    /// the owner ruling of 2026-08-12 (SPEC.md decision 12d) the rest after a completed carry-in
    /// package is measured in HOURS from the end of its last shift to the start of the next package's
    /// first shift, against <see cref="AutofillSpecConstants.MinRestHoursAfterCarryIn"/> — the
    /// configured rest days times 24, not calendar days. A gap of one free calendar day can therefore
    /// be conform (early ends 15:00, night starts 23:00 two days later = 56 h ≥ 48 h) while a 40-hour
    /// early-to-early turnaround over the same free day stays a violation. An employee whose plan
    /// holds no package after the continued one has no second block and nothing to measure.
    /// </summary>
    /// <param name="requiredRestHours">Rest hours that must follow the completed carry-in package</param>
    protected void AssertA12RestHoursFollowTheCompletedCarryInPackage(double requiredRestHours)
    {
        var problems = new List<string>();
        var shiftsByEmployee = AutofillPlanAnalyzer.BuildPlannedShifts(Run.Plan, Definition, includeCarryIn: true);

        foreach (var carryIn in Definition.OpenCarryIns)
        {
            var packages = Scenario2Diagnostics.PackagesOf(Metrics, carryIn.AgentId);
            var continued = packages.FirstOrDefault(
                p => p.StartDate <= Definition.PeriodFrom && p.EndDate >= Definition.PeriodFrom);
            if (continued is null)
            {
                problems.Add(
                    $"{carryIn.AgentId} holds no shift on {Definition.PeriodFrom:yyyy-MM-dd}, so the carried-in "
                    + $"package was not continued at all; its packages are "
                    + $"{Scenario2Diagnostics.DescribePackages(packages)}");
                continue;
            }

            var next = packages.FirstOrDefault(p => p.StartDate > continued.EndDate);
            if (next is null)
            {
                continue;
            }

            var shifts = shiftsByEmployee[carryIn.AgentId];
            var lastEnd = shifts
                .Where(s => s.Date >= continued.StartDate && s.Date <= continued.EndDate)
                .Max(s => s.EndAt);
            var nextStart = shifts
                .Where(s => s.Date >= next.StartDate)
                .Min(s => s.StartAt);
            var restHours = (nextStart - lastEnd).TotalHours;
            if (restHours < requiredRestHours)
            {
                problems.Add(
                    $"{carryIn.AgentId}: continued package {Scenario2Diagnostics.PackageLine(continued)} ends "
                    + $"{lastEnd:yyyy-MM-dd HH:mm} and the next package starts {nextStart:yyyy-MM-dd HH:mm} = "
                    + $"{restHours.ToString(NumberFormat, CultureInfo.InvariantCulture)} h of rest");
            }
        }

        problems.ShouldBeEmpty(
            $"A12: at least {requiredRestHours.ToString(CultureInfo.InvariantCulture)} hours of rest must follow "
            + "the completion of a carried-in package, measured from shift end to shift start (owner ruling "
            + "2026-08-12, SPEC.md decision 12d: the configured rest days between two work blocks count as "
            + $"{AutofillSpecConstants.HoursPerRestDay.ToString(CultureInfo.InvariantCulture)} hours each, not as "
            + "calendar days). Measured on the whole package that covers the first day of the period, February "
            + "part included. "
            + Describe(problems));
    }

    /// <summary>
    /// A13, reduced to a pinned measurement on 2026-08-12 (SPEC.md decision 11): the number of closed
    /// carry-in packages whose first package of the period fails to rotate must not exceed
    /// <see cref="MaxBoundaryRotationMisses"/>. The specification target of 0 stays binding in
    /// tests/autofill/SPEC.md and is quoted in the failure message; the boundary rotation was never
    /// implemented in the engine's fitness, which is why the assertion was permanently red and guarded
    /// nothing. Since the owner ruling 2026-08-12 (SPEC.md decision 12b) a first period package that
    /// starts at least the configured rest days times 24 hours after the closed package's last shift
    /// end is a free block restart and owes no rotation — only tighter restarts are judged.
    /// </summary>
    [Test]
    public void A13_ClosedPackagesRotateAcrossTheMonthBoundary()
    {
        var closed = Definition.CarryIns
            .Where(c => !c.IsOpenAt(Definition.PeriodFrom))
            .ToList();
        var problems = new List<string>();
        var shiftsByEmployee = AutofillPlanAnalyzer.BuildPlannedShifts(Run.Plan, Definition, includeCarryIn: true);

        foreach (var carryIn in closed)
        {
            var measured = Metrics.CarryIn
                .FirstOrDefault(c => string.Equals(c.Employee, carryIn.AgentId, StringComparison.Ordinal));
            if (measured is null)
            {
                problems.Add($"{carryIn.AgentId} was not measured at all, which means the fixture and the metrics disagree");
                continue;
            }

            if (measured.ActualFirstShiftType == carryIn.ExpectedFirstShiftKind)
            {
                continue;
            }

            var restHours = BoundaryRestHoursOf(carryIn.AgentId, shiftsByEmployee);
            if (restHours >= AutofillSpecConstants.MinRestHoursBetweenPackages)
            {
                continue;
            }

            var firstPackage = Scenario2Diagnostics.FirstPackageOf(Metrics, carryIn.AgentId);
            problems.Add(
                $"{carryIn.AgentId} closed a {carryIn.Kind} package on {carryIn.PackageEndInclusive:yyyy-MM-dd}, so "
                + $"its first package of the period must rotate to {carryIn.ExpectedFirstShiftKind}, but it is "
                + $"{(measured.ActualFirstShiftType?.ToString() ?? "absent — the employee received no in-period shift")} "
                + (restHours.HasValue
                    ? $"after only {restHours.Value.ToString(NumberFormat, CultureInfo.InvariantCulture)} h of rest "
                    : string.Empty)
                + $"({Scenario2Diagnostics.PackageLine(firstPackage)})");
        }

        problems.Count.ShouldBeLessThanOrEqualTo(
            MaxBoundaryRotationMisses,
            "A13: pinned measurement 2026-08-12 — at most "
            + $"{MaxBoundaryRotationMisses.ToString(CultureInfo.InvariantCulture)} of the closed carry-in packages "
            + "may fail to rotate. The specification target is 0 and stays binding (SPEC.md): a package that ended "
            + "in the previous month must not restart at the same kind; MA-4 rotates early to late and is expected "
            + $"to start again on {Scenario2SpecValues.Ma4ExpectedNewPackageStart:yyyy-MM-dd}, MA-5 rotates late to "
            + $"night and is expected to start again on {Scenario2SpecValues.Ma5ExpectedNewPackageStart:yyyy-MM-dd} "
            + "— the assertion itself only judges the shift kind, and since decision 12b only when the first "
            + "period package starts within "
            + $"{AutofillSpecConstants.MinRestHoursBetweenPackages.ToString(CultureInfo.InvariantCulture)} hours "
            + "of the closed package's last shift end; a later start is a free block restart. The engine's "
            + "fitness never scores a boundary rotation, so this is a target, not a guard, and only the count is "
            + "pinned. Same measurement on the auction seed plan: "
            + $"{Scenario2Diagnostics.DescribeSeedCarryIn(Run.SeedMetrics, closed.Select(c => c.AgentId))}. "
            + Describe(problems));
    }

    /// <summary>
    /// Rest between the last previous-month shift end and the first period shift start of one
    /// employee, in hours; null when either side holds no shift.
    /// </summary>
    private double? BoundaryRestHoursOf(
        string employee, IReadOnlyDictionary<string, IReadOnlyList<PlannedShift>> shiftsByEmployee)
    {
        if (!shiftsByEmployee.TryGetValue(employee, out var shifts))
        {
            return null;
        }

        DateTime? lastPreviousEnd = null;
        DateTime? firstPeriodStart = null;
        foreach (var shift in shifts)
        {
            if (shift.Date < Definition.PeriodFrom)
            {
                if (!lastPreviousEnd.HasValue || shift.EndAt > lastPreviousEnd.Value)
                {
                    lastPreviousEnd = shift.EndAt;
                }
            }
            else if (!firstPeriodStart.HasValue || shift.StartAt < firstPeriodStart.Value)
            {
                firstPeriodStart = shift.StartAt;
            }
        }

        if (!lastPreviousEnd.HasValue || !firstPeriodStart.HasValue)
        {
            return null;
        }

        return (firstPeriodStart.Value - lastPreviousEnd.Value).TotalHours;
    }

    [Test]
    public void A14_NothingBeforeThePeriodIsChanged()
    {
        var problems = new List<string>();

        if (!Run.CarryInUnchanged)
        {
            problems.Add($"the fixed previous-month works differ after the run: {Run.CarryInDifference}");
        }

        var outsidePeriod = AutofillPlanAnalyzer.TokensOutsidePeriod(Run.Plan, Definition);
        if (outsidePeriod.Count > 0)
        {
            problems.Add(
                $"{outsidePeriod.Count.ToString(CultureInfo.InvariantCulture)} assignment(s) carry a date outside the "
                + $"period: {string.Join("; ", outsidePeriod.Select(t => $"{t.AgentId} {t.Date:yyyy-MM-dd}"))}");
        }

        problems.ShouldBeEmpty(
            "A14: no shift dated before 2026-03-01 may be changed. Measured as the fixed February works being "
            + "byte-identical before and after the run — agent, day, span, hours, shift reference and work id — plus "
            + "no assignment being planned outside the period. The previous month is input only and never appears in "
            + $"the result scenario, so it cannot be compared inside the output. {Describe(problems)}");
    }

    /// <summary>One line out of the collected problem texts; empty when there is nothing to report.</summary>
    /// <param name="problems">Problem lines of one assertion</param>
    protected static string Describe(IReadOnlyList<string> problems)
        => problems.Count == 0 ? string.Empty : "Problems: " + string.Join(ProblemSeparator, problems);

    /// <summary>Carry-in measurements of the given employees, in metric order.</summary>
    /// <param name="employees">Employees to select</param>
    protected IReadOnlyList<CarryInRespect> CarryInEntriesOf(IReadOnlyList<string> employees)
    {
        var wanted = employees.ToHashSet(StringComparer.Ordinal);
        return Metrics.CarryIn.Where(c => wanted.Contains(c.Employee)).ToList();
    }
}
