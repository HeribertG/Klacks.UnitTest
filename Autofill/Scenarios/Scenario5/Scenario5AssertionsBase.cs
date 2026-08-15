// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Analysis;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Support;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario5;

/// <summary>
/// The assertions of specification scenario 5: S5-1 to S5-12, plus the inherited A1 to A15 and S4
/// families evaluated against this scenario's numbers — twelve assignments a day instead of nine,
/// seventeen ranks instead of fifteen, eleven carried-in packages instead of nine. Shared by every
/// scenario-5 run so the phase-D table compares the same judgements across the runs.
/// <para>
/// THE THREE-CLASS VIEW. Rotation, package purity and fairness are measured on the engine's three
/// shift classes, and a day shift counts there as a late shift. That is not a simplification the suite
/// chose but the only view that exists: the inference derives the class from the time span and
/// 08:00-16:00 is late. A package of day and late shifts is therefore NOT mixed, and the specification
/// accepts that explicitly. The day shift is visible only in the dayShift family, through its shift
/// reference id.
/// </para>
/// <para>
/// TWO ASSERTIONS ARE MARKED SpecFirstRed, and the reason is engine code rather than a measurement.
/// The blacklist half of the shift preferences is a hard veto in the AUCTION only. Every other path
/// that writes a token gates on <c>SlotConstraintFilter.IsValidAssignment</c>, which has no blacklist
/// check at all, and a blacklisted assignment is not even a <c>ViolationKind</c>, so nothing detects,
/// repairs or rejects one afterwards. The specification demands zero night shifts for the two
/// blacklisted employees; the engine has no mechanism that could guarantee it. The assertion is
/// written as the specification requires and carries the category, with the code references in the
/// message.
/// </para>
/// </summary>
public abstract class Scenario5AssertionsBase
{
    /// <summary>Separator between the individual problem lines of one assertion message.</summary>
    protected const string ProblemSeparator = " | ";

    /// <summary>
    /// The engine evidence behind the SpecFirstRed category of the blacklist assertions. Verified by
    /// reading the engine on 2026-08-14; no production code was changed.
    /// </summary>
    protected const string BlacklistGapEvidence =
        "Engine evidence (read-only survey 2026-08-14): the blacklist is a hard veto ONLY in the auction, at "
        + "Stage0HardConstraintChecker.cs:176 calling IsBlacklistedShift (274-292). Every other token-writing path "
        + "gates on SlotConstraintFilter.IsValidAssignment, which contains no blacklist check whatsoever "
        + "(SlotConstraintFilter.cs:28-116 — keyword at :72, nothing for ShiftPreferences). Blacklist-blind writers: "
        + "TokenRepair.cs:391 and :560 create new tokens behind :351/:536, and FillAllUnderSupply runs twice per "
        + "generation (TokenEvolutionLoop.cs:274,302) plus once per RuinRecreateMutation.cs:54; ReassignMutation.cs:39; "
        + "TopDownHandover.cs:173; SurplusHoursReturn.cs:228; ShiftKindBalancer.cs:325; and the auction's own "
        + "pre-seed CarryInContinuationSeeder.cs:143. Afterwards nothing detects it either: blacklist is not a member "
        + "of Constraints/ViolationKind.cs:10-24, so it never enters stage 0, is never repaired and never rejected — "
        + "it is only the soft score TokenFitnessEvaluator.cs:545 at weight 0.3. A plan with zero blacklisted "
        + "assignments is therefore possible but not guaranteed by any mechanism.";

    private const double ShareComparisonEpsilon = 1e-9;

    private const int MaxListedItems = 12;

    /// <summary>The scenario of the run the assertions read; guards its own fixture validity.</summary>
    protected abstract AutofillScenarioDefinition Definition { get; }

    /// <summary>The cached run of the fixture; guards its own fixture validity.</summary>
    protected abstract DeterministicRunResult Run { get; }

    /// <summary>Measurement of run 1 — the plan every assertion judges.</summary>
    protected AutofillMetrics Metrics => Run.Metrics;

    [Test]
    public void S5_1_EveryShiftOfEveryOrderIsStaffedAndEveryDayCarriesTwelve()
    {
        var coverage = Metrics.Coverage;
        var problems = new List<string>();

        if (coverage.UnfilledShifts.Count > 0)
        {
            problems.Add(
                $"{coverage.UnfilledShifts.Count.ToString(CultureInfo.InvariantCulture)} of "
                + $"{coverage.TotalRequiredShifts.ToString(CultureInfo.InvariantCulture)} demanded shifts are empty");
        }

        var starved = coverage.PerOrder.Where(o => o.Unfilled.Count > 0).ToList();
        if (starved.Count > 0)
        {
            problems.Add(
                "these orders are not covered on their own: "
                + string.Join(
                    ProblemSeparator,
                    starved.Select(o =>
                        $"order {o.Order.ToString(CultureInfo.InvariantCulture)}: "
                        + $"{o.FilledShifts.ToString(CultureInfo.InvariantCulture)}/"
                        + o.RequiredShifts.ToString(CultureInfo.InvariantCulture))));
        }

        var wrongDays = coverage.AssignmentsPerDay
            .Where(d => d.Count != Scenario5SpecConstants.AssignmentsPerDay)
            .ToList();
        if (wrongDays.Count > 0)
        {
            problems.Add(
                $"{wrongDays.Count.ToString(CultureInfo.InvariantCulture)} day(s) do not carry exactly "
                + $"{Scenario5SpecConstants.AssignmentsPerDay.ToString(CultureInfo.InvariantCulture)} assignments: "
                + string.Join(
                    ", ",
                    wrongDays.Take(MaxListedItems).Select(d =>
                        $"{d.Date:yyyy-MM-dd}={d.Count.ToString(CultureInfo.InvariantCulture)}")));
        }

        if (coverage.AssignmentsPerDay.Count != AutofillSpecConstants.PeriodDays)
        {
            problems.Add(
                $"the plan reports {coverage.AssignmentsPerDay.Count.ToString(CultureInfo.InvariantCulture)} days "
                + $"instead of {AutofillSpecConstants.PeriodDays.ToString(CultureInfo.InvariantCulture)}");
        }

        problems.ShouldBeEmpty(
            $"S5-1: all {Scenario5SpecConstants.TotalRequiredShifts.ToString(CultureInfo.InvariantCulture)} demanded "
            + $"shifts of {Definition.PeriodFrom:yyyy-MM-dd}..{Definition.PeriodUntil:yyyy-MM-dd} must be staffed and "
            + $"every one of the {AutofillSpecConstants.PeriodDays.ToString(CultureInfo.InvariantCulture)} days must "
            + $"carry exactly {Scenario5SpecConstants.AssignmentsPerDay.ToString(CultureInfo.InvariantCulture)} "
            + $"assignments — three orders times four shifts. {Describe(problems)}");
    }

    [Test]
    [Category("SpecFirstRed")]
    public void S5_2_TheBlacklistedEmployeesHoldNoNightShift()
    {
        var byEmployeeAlways = Metrics.Fairness.ShiftTypeCountPerEmployee
            .Where(c => Scenario5SpecConstants.DayShiftEmployees.Contains(c.Employee, StringComparer.Ordinal))
            .Select(c => $"{c.Employee}={c.Night.ToString(CultureInfo.InvariantCulture)}")
            .ToList();

        if (Definition.ShiftPreferences is null || Definition.ShiftPreferences.IsEmpty)
        {
            TestContext.Out.WriteLine(
                "S5-2 does not apply to this run: it carries no shift preferences, so nothing forbids a night shift "
                + "to anybody and a night shift here is correct behaviour, not a violation. Asserting a blacklist "
                + "against a run that has none would measure the fixture and not the engine. Night shifts held by the "
                + $"two employees the main run blacklists: {string.Join(", ", byEmployeeAlways)} — that count is the "
                + "size of the effect S5-2 asks the treated runs to remove completely.");
            Assert.Pass(
                "S5-2 is not applicable without a blacklist; the figure it would judge is reported above instead.");
        }

        var violations = Metrics.Preferences.BlacklistViolations;

        var nightOfEmployees = violations
            .Where(v => v.SlotKind == AutofillShiftKind.Night)
            .ToList();

        var problems = new List<string>();
        if (violations.Count > 0)
        {
            problems.Add(
                $"{violations.Count.ToString(CultureInfo.InvariantCulture)} assignment(s) stand on a blacklisted "
                + "shift: " + Scenario5Diagnostics.DescribeBlacklistViolations(violations.Take(MaxListedItems)));
        }

        var byEmployee = Definition.EmployeesInListOrder
            .Where(e => Scenario5SpecConstants.DayShiftEmployees.Contains(e, StringComparer.Ordinal))
            .Select(e => (Employee: e, Nights: Metrics.Fairness.ShiftTypeCountPerEmployee
                .Where(c => string.Equals(c.Employee, e, StringComparison.Ordinal))
                .Sum(c => c.Night)))
            .ToList();

        foreach (var entry in byEmployee.Where(e => e.Nights > 0))
        {
            problems.Add(
                $"{entry.Employee} holds {entry.Nights.ToString(CultureInfo.InvariantCulture)} night shift(s)");
        }

        problems.ShouldBeEmpty(
            $"S5-2: {string.Join(" and ", Scenario5SpecConstants.DayShiftEmployees)} are blacklisted from every night "
            + "shift of every order, so the plan must contain none — tolerance zero, in every run. Night shifts held "
            + $"by the two: {string.Join(", ", byEmployee.Select(e => $"{e.Employee}={e.Nights.ToString(CultureInfo.InvariantCulture)}"))}. "
            + $"Blacklisted assignments in total: {nightOfEmployees.Count.ToString(CultureInfo.InvariantCulture)}. "
            + $"{BlacklistGapEvidence} {Describe(problems)}");
    }

    [Test]
    public void S5_3_ThePreferenceWorksAsASoftPreferenceAndCostsNothingHarder()
    {
        var dayShift = Metrics.DayShift;
        var preferring = dayShift.ShareByEmployee.Where(s => s.PrefersDayShifts).ToList();
        var others = dayShift.ShareByEmployee.Where(s => !s.PrefersDayShifts).ToList();

        if (preferring.Count == 0)
        {
            TestContext.Out.WriteLine(
                "S5-3 has no preferring employee in this run, so the floor below is vacuous and the assertion proves "
                + "nothing about the engine. Reported instead, as the control figure the treated run is compared "
                + $"against: {Scenario5Diagnostics.DescribeDayShiftShares(dayShift.ShareByEmployee)}");
            Assert.Pass("S5-3 is not applicable without a preference; the control shares are reported above instead.");
        }

        var problems = new List<string>();

        if (Metrics.Preferences.FalsePositiveViolations.Count > 0)
        {
            problems.Add(
                $"{Metrics.Preferences.FalsePositiveViolations.Count.ToString(CultureInfo.InvariantCulture)} "
                + "preferred assignment(s) take part in a hard finding: "
                + Scenario5Diagnostics.DescribeFalsePositives(
                    Metrics.Preferences.FalsePositiveViolations.Take(MaxListedItems)));
        }

        var meanOfOthers = others.Count == 0 ? 0 : others.Average(s => s.Share);
        foreach (var entry in preferring.Where(s => s.Share + ShareComparisonEpsilon < meanOfOthers))
        {
            problems.Add(
                $"{entry.Employee} holds a day-shift share of "
                + $"{entry.Share.ToString("0.###", CultureInfo.InvariantCulture)}, below the "
                + $"{meanOfOthers.ToString("0.###", CultureInfo.InvariantCulture)} the non-preferring employees "
                + "average");
        }

        problems.ShouldBeEmpty(
            "S5-3: the day-shift preference is a SOFT term — it tilts the bidding, it is not a rule — so the only two "
            + "things asserted are that it had an effect at all (each preferring employee reaches at least the "
            + "per-head day-shift share of the employees who never asked for one) and that granting it never cost "
            + "anything harder. It is expressly NOT a violation that most day shifts go to non-preferring employees: "
            + $"{Scenario5SpecConstants.TotalDayShifts.ToString(CultureInfo.InvariantCulture)} day shifts exist and the "
            + "two preferring employees can work about "
            + $"{Scenario5SpecConstants.FlatShareShifts.ToString(CultureInfo.InvariantCulture)} shifts each in total. "
            + $"Measured shares: {Scenario5Diagnostics.DescribeDayShiftShares(dayShift.ShareByEmployee)}. "
            + $"{Describe(problems)}");
    }

    [Test]
    public void S5_4_TheKeywordWindowsAreRespected()
    {
        var violations = Metrics.Keyword.ScheduleCommandViolations;
        var windows = Metrics.Keyword.Windows;
        var declared = Definition.ScheduleCommands?.Commands.Count ?? 0;
        var problems = new List<string>();

        if (violations.Count > 0)
        {
            problems.Add(
                $"{violations.Count.ToString(CultureInfo.InvariantCulture)} assignment(s) break their keyword: "
                + Scenario5Diagnostics.DescribeCommandViolations(violations.Take(MaxListedItems)));
        }

        if (windows.Count != declared)
        {
            problems.Add(
                $"the measurement reports {windows.Count.ToString(CultureInfo.InvariantCulture)} window(s) but the "
                + $"fixture declares {declared.ToString(CultureInfo.InvariantCulture)}");
        }

        if (declared == 0)
        {
            TestContext.Out.WriteLine(
                "S5-4 on a run without keyword windows: nothing to respect, so this assertion only confirms that the "
                + "measurement is empty rather than accidentally populated from another run.");
        }

        problems.ShouldBeEmpty(
            "S5-4: the schedule commands are hard. Both FREE windows must be completely empty and every Only* window "
            + "may hold only shifts of the named class, the day shift counting as late. The window list is reported "
            + "next to the violations because an honoured FREE window produces no entry anywhere and would otherwise "
            + $"be indistinguishable from a window that was never applied: {Scenario5Diagnostics.DescribeWindows(windows)}. "
            + $"{Describe(problems)}");
    }

    [Test]
    public void S5_5_TheElevenCarriedInPackagesAreContinuedAndTheFreeSlotIsStaffed()
    {
        var carryIn = Metrics.CarryInThreeDimensional;
        var problems = new List<string>();

        var broken = carryIn.Where(c => !c.Ok).ToList();
        if (broken.Count > 0)
        {
            problems.Add(
                $"{broken.Count.ToString(CultureInfo.InvariantCulture)} package(s) were not continued as declared: "
                + Scenario5Diagnostics.DescribeCarryIn(broken.Take(MaxListedItems)));
        }

        if (carryIn.Count != Definition.CarryIns.Count)
        {
            problems.Add(
                $"the measurement covers {carryIn.Count.ToString(CultureInfo.InvariantCulture)} package(s) but the "
                + $"fixture declares {Definition.CarryIns.Count.ToString(CultureInfo.InvariantCulture)}");
        }

        var freeSlot = AutofillShiftCatalog.ShiftIdOf(
            Scenario5SpecConstants.OrderCount, AutofillShiftKind.Day);
        var staffedFreshly = Run.Plan.Tokens.Any(
            t => t.Date == Definition.PeriodFrom && t.ShiftRefId == freeSlot);
        if (!staffedFreshly)
        {
            problems.Add(
                $"the day shift of order {Scenario5SpecConstants.OrderCount.ToString(CultureInfo.InvariantCulture)} on "
                + $"{Definition.PeriodFrom:yyyy-MM-dd} is the one slot without a carry-in and stayed empty");
        }

        problems.ShouldBeEmpty(
            $"S5-5: the {Scenario5SpecConstants.OpenCarryInCount.ToString(CultureInfo.InvariantCulture)} running "
            + "packages must be continued on the same order and the same SLOT for exactly the declared number of days "
            + "— slot, not class, because one order now holds two slots of the late class and continuing a day-shift "
            + "package on the late shift of the same order is not a continuation. The one uncovered slot, the day "
            + "shift of the third order on the first day, must be staffed freshly. All packages: "
            + $"{Scenario5Diagnostics.DescribeCarryIn(carryIn)}. {Describe(problems)}");
    }

    [Test]
    public void S5_6_EveryDayShiftIsTraceableAndCountsAsALateShift()
    {
        var dayShift = Metrics.DayShift;
        var problems = new List<string>();

        var inPeriod = dayShift.Assignments.Where(a => !a.IsCarryIn).ToList();
        if (inPeriod.Count != Scenario5SpecConstants.TotalDayShifts)
        {
            problems.Add(
                $"the plan holds {inPeriod.Count.ToString(CultureInfo.InvariantCulture)} in-period day shift(s) "
                + $"instead of {Scenario5SpecConstants.TotalDayShifts.ToString(CultureInfo.InvariantCulture)}");
        }

        var unresolved = dayShift.Assignments
            .Where(a => AutofillShiftCatalog.SlotKindOf(a.ShiftRefId) != AutofillShiftKind.Day)
            .ToList();
        if (unresolved.Count > 0)
        {
            problems.Add(
                $"{unresolved.Count.ToString(CultureInfo.InvariantCulture)} day-shift entry does not resolve back to a "
                + "day shift through its shift reference id");
        }

        var classified = Run.Plan.Tokens
            .Where(t => AutofillShiftCatalog.IsDayShift(t.ShiftRefId))
            .Where(t => AutofillShiftCatalog.FromShiftTypeIndex(t.ShiftTypeIndex) != AutofillShiftKind.Late)
            .ToList();
        if (classified.Count > 0)
        {
            problems.Add(
                $"{classified.Count.ToString(CultureInfo.InvariantCulture)} day-shift token(s) do not carry the late "
                + "shift class, so rotation and fairness would count them as something else");
        }

        var shareSum = dayShift.ShareByEmployee.Sum(s => s.DayShiftCount);
        if (shareSum != inPeriod.Count)
        {
            problems.Add(
                $"the per-employee shares add up to {shareSum.ToString(CultureInfo.InvariantCulture)} day shifts but "
                + $"{inPeriod.Count.ToString(CultureInfo.InvariantCulture)} were planned");
        }

        problems.ShouldBeEmpty(
            "S5-6: every day-shift assignment must be traceable through its shift reference id and must appear to "
            + "rotation, purity and fairness as a LATE shift — the engine infers the class from the span and "
            + $"{AutofillSpecConstants.DayStartTime}-{AutofillSpecConstants.DayEndTime} is late. A day shift carrying "
            + "another class would mean the inference changed and every three-class expectation of this scenario "
            + $"measures something else. {Describe(problems)}");
    }

    [Test]
    public void S5_7_GuaranteedHoursAreServedTopDownOverSeventeenRanks()
    {
        var hours = Metrics.Hours;
        var problems = new List<string>();

        if (hours.MonotonicityViolations.Count > 0)
        {
            problems.Add(
                "the fulfilment rises against the list order at "
                + Scenario5Diagnostics.DescribeMonotonicity(hours.MonotonicityViolations));
        }

        if (hours.PerEmployee.Count != Definition.EmployeesInListOrder.Count)
        {
            problems.Add(
                $"the measurement covers {hours.PerEmployee.Count.ToString(CultureInfo.InvariantCulture)} ranks "
                + "instead of the "
                + $"{Definition.EmployeesInListOrder.Count.ToString(CultureInfo.InvariantCulture)} the roster holds");
        }

        problems.ShouldBeEmpty(
            "S5-7: the guaranteed hours must be served in list order over all "
            + $"{Scenario5SpecConstants.EmployeeCount.ToString(CultureInfo.InvariantCulture)} ranks, so the fulfilment "
            + "never rises going down the list. The tolerance band is owner ruling B5 — one daily working time of the "
            + "checked employee above the minimum of all better ranks — and the analyzer applies it. Which of the two "
            + "top-down resolutions the algorithm picked is MEASURED and reported, never asserted; see the diagnosis "
            + "block of this run for both references and for the FREE-adjusted per-rank ceilings, which say whether a "
            + $"gap at rank 1 or 2 comes from the fixture rather than the algorithm. {Scenario5Diagnostics.DescribeHours(hours.PerEmployee)}. "
            + $"{Describe(problems)}");
    }

    [Test]
    public void S5_8_RotationRunsForwardWithTheDayShiftCountedAsLate()
    {
        var rotation = Metrics.Rotation;

        rotation.ForwardRate.ShouldBeGreaterThanOrEqualTo(
            Scenario5SpecConstants.MinForwardRotationRate,
            "S5-8: at least "
            + (Scenario5SpecConstants.MinForwardRotationRate * 100).ToString("0", CultureInfo.InvariantCulture)
            + " % of the package transitions must rotate forwards, early to late to night to early, with a day shift "
            + "counted as a late shift. Measured "
            + rotation.ForwardRate.ToString("0.###", CultureInfo.InvariantCulture)
            + $" over {rotation.Transitions.Count.ToString(CultureInfo.InvariantCulture)} transition(s), of which "
            + $"{rotation.BackwardOrSkipCount.ToString(CultureInfo.InvariantCulture)} run backwards or skip a class "
            + $"and {rotation.RestSeparatedCount.ToString(CultureInfo.InvariantCulture)} are separated by a full rest "
            + "and owe no rotation.");
    }

    [Test]
    public void S5_9_NothingBeforeThePeriodChangesAndTheRunReproducesItself()
    {
        var problems = new List<string>();

        if (!Run.CarryInUnchanged)
        {
            problems.Add($"the previous month changed: {Run.CarryInDifference}");
        }

        var outside = AutofillPlanAnalyzer.TokensOutsidePeriod(Run.Plan, Definition);
        if (outside.Count > 0)
        {
            problems.Add(
                $"{outside.Count.ToString(CultureInfo.InvariantCulture)} assignment(s) lie outside the period");
        }

        if (!Run.RunsIdentical)
        {
            problems.Add($"two runs with the same seed differ at {Run.FirstDifference}");
        }

        problems.ShouldBeEmpty(
            "S5-9: the carry-in month is fixed input and must be byte-identical before and after the run, nothing may "
            + "be planned outside the period, and two runs with the same seed must produce the same plan. A run that "
            + $"differs from itself makes every other assertion meaningless. {Describe(problems)}");
    }

    [Test]
    public void S5_10_TheRhythmAndTheTwoCohortsAreBalanced()
    {
        var problems = new List<string>();
        var packages = Metrics.Packages;

        var mode = packages.FreeBlockHistogram.Count == 0
            ? 0
            : packages.FreeBlockHistogram.OrderByDescending(e => e.Value).ThenBy(e => e.Key).First().Key;
        if (mode != Scenario5SpecConstants.ExpectedFreeBlockMode)
        {
            problems.Add(
                $"the free-block histogram peaks at {mode.ToString(CultureInfo.InvariantCulture)} days instead of "
                + $"{Scenario5SpecConstants.ExpectedFreeBlockMode.ToString(CultureInfo.InvariantCulture)}: "
                + Scenario5Diagnostics.DescribeFreeBlocks(packages.FreeBlockHistogram));
        }

        var spread = Metrics.Fairness.SpreadPerType;
        if (spread.Early > Scenario5SpecConstants.MaxShiftKindSpread
            || spread.Late > Scenario5SpecConstants.MaxShiftKindSpread)
        {
            problems.Add(
                $"the early/late spread is {spread.Early.ToString(CultureInfo.InvariantCulture)}/"
                + $"{spread.Late.ToString(CultureInfo.InvariantCulture)}, above "
                + Scenario5SpecConstants.MaxShiftKindSpread.ToString(CultureInfo.InvariantCulture));
        }

        var nightCohort = Metrics.Fairness.ShiftTypeCountPerEmployee
            .Where(c => !Scenario5SpecConstants.DayShiftEmployees.Contains(c.Employee, StringComparer.Ordinal))
            .ToList();
        var nightSpread = nightCohort.Count == 0
            ? 0
            : nightCohort.Max(c => c.Night) - nightCohort.Min(c => c.Night);
        if (nightSpread > Scenario5SpecConstants.MaxShiftKindSpread)
        {
            problems.Add(
                $"inside the night cohort of "
                + $"{nightCohort.Count.ToString(CultureInfo.InvariantCulture)} employees the night spread is "
                + $"{nightSpread.ToString(CultureInfo.InvariantCulture)}, above "
                + Scenario5SpecConstants.MaxShiftKindSpread.ToString(CultureInfo.InvariantCulture));
        }

        problems.ShouldBeEmpty(
            "S5-10: the work share of "
            + (Scenario5SpecConstants.RequiredWorkShare * 100).ToString("0.0", CultureInfo.InvariantCulture)
            + " % lies just below the 5/2 ideal of "
            + (Scenario5SpecConstants.IdealFiveTwoWorkShare * 100).ToString("0.00", CultureInfo.InvariantCulture)
            + " %, so free blocks of two days must dominate. The night spread is measured over the "
            + $"{Scenario5SpecConstants.NightPoolSize.ToString(CultureInfo.InvariantCulture)}-employee cohort that may "
            + "take a night shift at all — including the two blacklisted employees would compare people who are "
            + "forbidden the shift with people who are not, and would read as unfairness where the fixture is the "
            + $"cause. Counts early/late/night: {Scenario5Diagnostics.DescribeShiftTypeCounts(Metrics.Fairness.ShiftTypeCountPerEmployee)}. "
            + $"{Describe(problems)}");
    }

    [Test]
    public void S5_12_TheRunFinishedAndItsRuntimeIsReported()
    {
        var runtime = Metrics.Runtime;
        var seconds = runtime.WallClockMs / 1000.0;

        TestContext.Out.WriteLine(
            $"S5-12 runtime {seconds.ToString("0.0", CultureInfo.InvariantCulture)} s, ratio against scenario 2 "
            + runtime.RatioVsScenario2.ToString("0.##", CultureInfo.InvariantCulture));

        if (seconds > Scenario5SpecConstants.RuntimeFindingSeconds)
        {
            TestContext.Out.WriteLine(
                $"S5-12 finding (informative, not a failure): the run took longer than "
                + $"{Scenario5SpecConstants.RuntimeFindingSeconds.ToString(CultureInfo.InvariantCulture)} s.");
        }

        runtime.WallClockMs.ShouldBeGreaterThan(
            0,
            "S5-12: the wall clock of the run must be measured and reported. A run above "
            + $"{Scenario5SpecConstants.RuntimeFindingSeconds.ToString(CultureInfo.InvariantCulture)} s is an "
            + "informative finding and never a failure — this assertion only fails when nothing was measured at all.");
    }

    [Test]
    public void A2_S4_2_NobodyHoldsTwoConflictingAssignments()
    {
        var coverage = Metrics.Coverage;
        var problems = new List<string>();

        if (coverage.DoubleBookings.Count > 0)
        {
            problems.Add(
                $"{coverage.DoubleBookings.Count.ToString(CultureInfo.InvariantCulture)} conflicting pair(s) of "
                + "assignments: "
                + string.Join(
                    ", ",
                    coverage.DoubleBookings.Take(MaxListedItems).Select(b =>
                        $"{b.Employee} {b.Date:yyyy-MM-dd} ({b.Reason})")));
        }

        if (coverage.CrossOrderDoubleBookings.Count > 0)
        {
            problems.Add(
                $"{coverage.CrossOrderDoubleBookings.Count.ToString(CultureInfo.InvariantCulture)} day(s) on which "
                + "one employee holds shifts of more than one order");
        }

        if (coverage.OversuppliedSlots > 0)
        {
            problems.Add(
                $"{coverage.OversuppliedSlots.ToString(CultureInfo.InvariantCulture)} slot(s) received more than one "
                + "employee");
        }

        problems.ShouldBeEmpty(
            "A2/S4-2: no shift may be staffed twice and no employee may hold two shifts whose times overlap, across "
            + "order boundaries included. Scenario 5 makes this sharper than scenario 4 did: the day shift "
            + $"{AutofillSpecConstants.DayStartTime}-{AutofillSpecConstants.DayEndTime} OVERLAPS the early shift and "
            + "runs into the last hour of the late shift, so for the first time in the suite two shifts of the same "
            + $"day genuinely can collide. {Describe(problems)}");
    }

    [Test]
    public void A3_S4_4_RestTimeAndNightToEarlyAreRespectedAcrossOrders()
    {
        var legality = Metrics.Legality;
        var problems = new List<string>();

        if (legality.RestViolations.Count > 0)
        {
            problems.Add(
                $"{legality.RestViolations.Count.ToString(CultureInfo.InvariantCulture)} rest-time violation(s): "
                + string.Join(
                    ", ",
                    legality.RestViolations.Take(MaxListedItems).Select(v =>
                        $"{v.Employee} {v.DateFrom:MM-dd} {v.FromShift} to {v.DateTo:MM-dd} {v.ToShift} = "
                        + $"{v.GapHours.ToString("0.#", CultureInfo.InvariantCulture)} h")));
        }

        if (legality.NightToEarlyViolations.Count > 0)
        {
            problems.Add(
                $"{legality.NightToEarlyViolations.Count.ToString(CultureInfo.InvariantCulture)} night-to-early "
                + "transition(s)");
        }

        problems.ShouldBeEmpty(
            $"A3/S4-4: the rest time of {AutofillSpecConstants.MinRestHours.ToString(CultureInfo.InvariantCulture)} h "
            + "must hold between any two shifts of one employee, order boundaries and the month seam included, and a "
            + "night shift may never be followed by an early one. Two transitions of this scenario are illegal by "
            + "arithmetic and the engine is expected to avoid them: night to day gives 1 h and late to day gives 9 h, "
            + $"both below the {AutofillSpecConstants.MinRestHours.ToString(CultureInfo.InvariantCulture)} h "
            + $"required. {Describe(problems)}");
    }

    [Test]
    public void A4_ShiftClassStaysConstantInsideAPackage()
    {
        var mixed = Metrics.Packages.MixedTypeCount;

        mixed.ShouldBe(
            0,
            "A4: inside one work package the shift CLASS must not change. The day shift does not weaken this rule and "
            + "does not test it either: it carries the late class, so a package alternating day and late shifts is "
            + "pure to this measure by construction, which the specification accepts explicitly. What this assertion "
            + $"still catches is a package mixing early, late and night. Packages: "
            + $"{Metrics.Packages.Items.Count.ToString(CultureInfo.InvariantCulture)}, of them "
            + $"{mixed.ToString(CultureInfo.InvariantCulture)} mixed.");
    }

    [Test]
    public void A5_PackageLengthsFollowTheFiveTwoIdeal()
    {
        var packages = Metrics.Packages;
        var problems = new List<string>();

        var tooLong = AutofillPlanAnalyzer.PackagesLongerThan(packages, AutofillSpecConstants.MaxAllowedPackageLength);
        if (tooLong > 0)
        {
            problems.Add(
                $"{tooLong.ToString(CultureInfo.InvariantCulture)} package(s) run longer than "
                + $"{AutofillSpecConstants.MaxAllowedPackageLength.ToString(CultureInfo.InvariantCulture)} days");
        }

        var shortShare = AutofillPlanAnalyzer.ShortPackageShare(
            packages, AutofillSpecConstants.ShortPackageMaxLength);
        var limit = AutofillSpecConstants.ShortPackageShareLimit + AutofillSpecConstants.ShortPackageShareTolerance;
        if (shortShare > limit + ShareComparisonEpsilon)
        {
            problems.Add(
                $"{(shortShare * 100).ToString("0.#", CultureInfo.InvariantCulture)} % of the packages are at most "
                + $"{AutofillSpecConstants.ShortPackageMaxLength.ToString(CultureInfo.InvariantCulture)} days long, "
                + $"above the {(limit * 100).ToString("0.#", CultureInfo.InvariantCulture)} % still accepted");
        }

        problems.ShouldBeEmpty(
            "A5: work packages follow the five-work rhythm — none longer than "
            + $"{AutofillSpecConstants.MaxAllowedPackageLength.ToString(CultureInfo.InvariantCulture)} days, and only "
            + "a small share of very short ones. Packages that cross the month boundary count their February days, "
            + "so a carried-in package legitimately reaches its full length inside the period. Length histogram: "
            + string.Join(
                ", ",
                packages.LengthHistogram
                    .OrderBy(e => e.Key, StringComparer.Ordinal)
                    .Select(e => $"{e.Key}d x {e.Value.ToString(CultureInfo.InvariantCulture)}"))
            + $". {Describe(problems)}");
    }

    [Test]
    public void A13_S4_8_ClosedPackagesRotateAcrossTheMonthBoundary()
    {
        var closed = Metrics.CarryInThreeDimensional
            .Where(c => c.ExpectedRemainingDays == 0)
            .ToList();

        var problems = closed
            .Where(c => c.ActualShiftType is not null && c.ActualShiftType != c.ExpectedShiftType)
            .Select(c =>
                $"{c.Employee} owed {c.ExpectedShiftType} but started on {c.ActualShiftType}"
                + (c.ActualFirstSlotKind == AutofillShiftKind.Day ? " (a day shift, which IS the late class)" : string.Empty))
            .ToList();

        problems.ShouldBeEmpty(
            $"A13/S4-8: the {Scenario5SpecConstants.ClosedCarryInCount.ToString(CultureInfo.InvariantCulture)} "
            + "packages that closed in February owe the period a rotated shift CLASS — early to late to night to "
            + "early — and no particular order, because the specification's rotation table names no order. A day "
            + "shift satisfies an owed late, which is the direct consequence of the scenario-5 decision to treat it "
            + $"as one. {Describe(problems)}");
    }

    [Test]
    public void S4_5_OrderLoyaltyHoldsInsideThePackage()
    {
        var switches = Metrics.Orders.SwitchesWithinPackage;
        var pure = Metrics.Packages.Items.Count - switches.Count;

        // Owner ruling 2026-08-14: measured and reported, never asserted — scenario 4's spec
        // defined S4-5/S4-6 as measurements and the fourth slot column shifts the achievable
        // level; a hard zero here would judge a ceiling nobody has decided for this setup.
        TestContext.Out.WriteLine(
            $"S4-5 (measurement, owner ruling 2026-08-14): {switches.Count.ToString(CultureInfo.InvariantCulture)} "
            + $"of {Metrics.Packages.Items.Count.ToString(CultureInfo.InvariantCulture)} packages touch more than "
            + $"one order, {pure.ToString(CultureInfo.InvariantCulture)} stay on one. Packages touching several "
            + "orders: "
            + string.Join(
                ", ",
                switches.Take(MaxListedItems).Select(s =>
                    $"{s.Employee} from {s.PackageStart:yyyy-MM-dd} over orders "
                    + string.Join("+", s.Orders.Select(o => o.ToString(CultureInfo.InvariantCulture))))));

        Assert.Pass(
            $"S4-5 measured: {switches.Count.ToString(CultureInfo.InvariantCulture)} packages touch several orders "
            + "(reported, not judged).");
    }

    [Test]
    public void S4_6_OrderLoyaltyHoldsAcrossPackages()
    {
        var perEmployee = Metrics.Orders.SwitchesPerEmployee;
        var median = Median(perEmployee.Select(s => s.SwitchCount).ToList());

        TestContext.Out.WriteLine(
            "S4-6 order switches per employee: "
            + string.Join(
                ", ",
                perEmployee.Select(s =>
                    $"{s.Employee}={s.SwitchCount.ToString(CultureInfo.InvariantCulture)}")));

        // Owner ruling 2026-08-14: measured and reported, never asserted (see S4-5 above). The
        // scenario-4 reference median of 2 stays in the log line as the comparison anchor.
        Assert.Pass(
            $"S4-6 measured: order-switch median {median.ToString(CultureInfo.InvariantCulture)} against the "
            + $"scenario-4 reference {Scenario5SpecConstants.MaxOrderSwitchMedian.ToString(CultureInfo.InvariantCulture)} "
            + "(reported, not judged).");
    }

    /// <summary>Joins the collected problem lines into one readable clause.</summary>
    /// <param name="problems">Collected problem messages</param>
    protected static string Describe(IReadOnlyList<string> problems)
        => problems.Count == 0 ? "No problem found." : "Found: " + string.Join(ProblemSeparator, problems);

    private static int Median(IReadOnlyList<int> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }
}
