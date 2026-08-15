// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Analysis;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario5;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario6;

/// <summary>
/// The fast half of scenario 6: everything that can be decided from the FIXTURE, without starting the
/// engine once. Eight engine runs are hours of wall clock and belong to phase C; these tests are
/// seconds and run in the ordinary suite, because a fixture mistake found here costs nothing while the
/// same mistake found after eight runs costs all of them.
/// <para>
/// They also cover the one failure mode a scenario-6 assertion can have that looks like success: the
/// stored control run is missing, the comparison finds nothing, and "nothing" is reported as "no
/// difference". <see cref="TheComparisonReportsAMissingControlRunRatherThanNoDifference"/> points the
/// reader at a path that cannot exist and proves the state is NotAvailable and the diff empty.
/// </para>
/// </summary>
[TestFixture]
[Category("Autofill")]
[Category("Scenario6")]
public class Scenario6FixtureGuardTests
{
    private const string NonexistentBaselinePath = "/klacks-scenario6-baseline-that-cannot-exist.metrics.json";

    private const double Tolerance = 1e-9;

    /// <summary>Every scenario-6 run, with the name the report uses.</summary>
    private static IEnumerable<TestCaseData> AllRuns()
    {
        yield return new TestCaseData(
            (Func<AutofillScenarioDefinition>)Scenario6Fixture.BuildMainRun).SetName("S6a");
        yield return new TestCaseData(
            (Func<AutofillScenarioDefinition>)Scenario6Fixture.BuildCalibrationRun).SetName("S6b");
        yield return new TestCaseData(
            (Func<AutofillScenarioDefinition>)Scenario6Fixture.BuildTopRankRun).SetName("S6c");
        yield return new TestCaseData(
            (Func<AutofillScenarioDefinition>)Scenario6Fixture.BuildCrossMonthRun).SetName("S6d");
        yield return new TestCaseData(
            (Func<AutofillScenarioDefinition>)Scenario6Fixture.BuildKeywordCoveredRun).SetName("S6e");
        yield return new TestCaseData(
            (Func<AutofillScenarioDefinition>)Scenario6Fixture.BuildBothPreferenceRun).SetName("S6f");
        yield return new TestCaseData(
            (Func<AutofillScenarioDefinition>)Scenario6Fixture.BuildUnsolvableRun).SetName("S6g");
    }

    [Test]
    [TestCaseSource(nameof(AllRuns))]
    public void EveryRunAssemblesAndPassesItsGuard(Func<AutofillScenarioDefinition> build)
    {
        var definition = build();
        var problems = Scenario6FixtureGuard.Validate(definition);

        foreach (var note in Scenario6FixtureGuard.Notes(definition))
        {
            TestContext.Out.WriteLine("note: " + note);
        }

        problems.ShouldBeEmpty(
            "A scenario-6 run whose fixture does not match the specification would make every rule assertion of that "
            + "run a statement about the wrong setup. " + Scenario6FixtureGuard.Describe(problems));
    }

    /// <summary>
    /// The holiday of the main run, checked against every number the specification states: fourteen
    /// one-day rows, exactly two Monday-to-Sunday weeks, ten paid and four unpaid, and a credit of
    /// 81.82 h — with the daily value pinned by IDENTITY. The identity check is the load-bearing one:
    /// 8.1818 sums to 81.818, well inside a hundredth of the expected credit, so a sum tolerance alone
    /// cannot tell the reference value from a rounded literal.
    /// </summary>
    [Test]
    public void TheHolidayOfTheMainRunIsFourteenOneDayRowsWithTenPaidOnes()
    {
        var definition = Scenario6Fixture.BuildMainRun();
        var rows = definition.Absences.Rows;

        rows.Count.ShouldBe(Scenario6SpecConstants.HolidayRows, "the holiday must be one row per calendar day");
        rows.Count(r => r.GrantsCredit).ShouldBe(Scenario6SpecConstants.HolidayPaidRows);
        rows.Count(r => !r.GrantsCredit).ShouldBe(Scenario6SpecConstants.HolidayUnpaidRows);

        rows.ShouldAllBe(
            r => !r.GrantsCredit || r.Hours == Scenario6SpecConstants.DailyCreditHours,
            "every paid row must carry 180/22 h exactly, constructed as a decimal division");

        rows.Where(r => !r.GrantsCredit).ShouldAllBe(
            r => r.Date.DayOfWeek == DayOfWeek.Saturday || r.Date.DayOfWeek == DayOfWeek.Sunday,
            "only weekend rows may be unpaid; the divisor of the daily value is the month's working days");

        definition.Context.BreakBlockers.ShouldAllBe(
            b => b.FromInclusive == b.UntilInclusive,
            "owner decision O2: one blocker per day, exactly as WizardHardConstraintBuilder builds them");

        var credit = (double)definition.Absences.CreditHoursOf(Scenario6SpecConstants.HolidayEmployee);
        credit.ShouldBe(
            (double)Scenario6SpecConstants.WindowCreditHours,
            Scenario6SpecConstants.CreditTolerance,
            "ten working days at 180/22 h credit 81.82 h");

        TestContext.Out.WriteLine(
            $"holiday credit: {credit.ToString("0.####", CultureInfo.InvariantCulture)} h from "
            + $"{Scenario6SpecConstants.HolidayPaidRows.ToString(CultureInfo.InvariantCulture)} paid rows at "
            + $"{Scenario6SpecConstants.DailyCreditHours.ToString(CultureInfo.InvariantCulture)} h");
    }

    /// <summary>
    /// The holiday window is exactly two calendar weeks. Every other number of the run follows from
    /// that shape: ten working days, four weekend days, and a window that starts and ends on a rhythm
    /// boundary rather than in the middle of a week.
    /// </summary>
    [Test]
    public void TheHolidayWindowIsExactlyTwoCalendarWeeks()
    {
        Scenario6SpecConstants.HolidayFrom.DayOfWeek.ShouldBe(DayOfWeek.Monday);
        Scenario6SpecConstants.HolidayUntil.DayOfWeek.ShouldBe(DayOfWeek.Sunday);
        (Scenario6SpecConstants.HolidayUntil.DayNumber - Scenario6SpecConstants.HolidayFrom.DayNumber + 1)
            .ShouldBe(Scenario6SpecConstants.HolidayDays);

        var definition = Scenario6Fixture.BuildMainRun();
        Scenario6FixtureGuard
            .ValidateTwoWeekWindow(definition, Scenario6SpecConstants.HolidayEmployee)
            .ShouldBeEmpty();
    }

    /// <summary>
    /// The availability arithmetic the specification requires the test to compute and protocol before
    /// every run: 513 places, 72.5 % work share, about six forced extra shifts, 224 places inside the
    /// window, and a tightest day at 80 %.
    /// </summary>
    [Test]
    public void TheAvailabilityArithmeticOfTheMainRunMatchesTheSpecification()
    {
        var definition = Scenario6Fixture.BuildMainRun();
        var availability = AbsenceAnalyzer.BuildAvailability(definition);

        TestContext.Out.WriteLine(
            "S6a availability: " + Scenario6Diagnostics.DescribeAvailability(availability));

        availability.SlotsTotal.ShouldBe(
            Scenario6SpecConstants.MainRunSlotsTotal,
            "16 employees over 31 days plus the absent one's 17 remaining days = 513 places");
        availability.RequiredAssignments.ShouldBe(Scenario5SpecConstants.TotalRequiredShifts);
        availability.WorkRatioRequired.ShouldBe(0.725, 0.001);
        availability.MaxRatioUnder52.ShouldBe(5.0 / 7.0, Tolerance);
        availability.ForcedExtraShifts.ShouldBe(Scenario6SpecConstants.ExpectedForcedExtraShifts);
        availability.AbsenceWindowSlots.ShouldBe(224, "16 employees over the 14 window days");
        availability.AbsenceWindowRequired.ShouldBe(168, "12 assignments over the 14 window days");
        availability.AbsenceWindowWorkRatio.ShouldBe(0.75, 0.001);

        availability.TightestDay.ShouldNotBeNull();
        availability.TightestDay!.AvailableEmployees.ShouldBe(
            15,
            "on 10 to 12 March the absent employee and the FREE window of MA-01 overlap, leaving fifteen");
        availability.TightestDay.Ratio.ShouldBe(0.8, 0.001);
    }

    /// <summary>
    /// The day shifts inside the window that necessarily go to employees who never asked for one. The
    /// specification names 32 for the main run and all 42 for the run without both preference
    /// employees; both fall out of the same derivation, which is what this test proves.
    /// </summary>
    [Test]
    public void TheDayShiftSpillOverFloorMatchesTheSpecificationForBothRuns()
    {
        Scenario6SpecConstants
            .DayShiftsThatMustSpillOver(Scenario6Fixture.BuildMainRun(), Scenario6SpecConstants.HolidayDays)
            .ShouldBe(
                Scenario6SpecConstants.MinDayShiftsToNonPreferring,
                "one preference employee remains and can work about ten of the fourteen days, so at least 32 of the "
                + "42 day shifts inside the window go elsewhere");

        Scenario6SpecConstants
            .DayShiftsThatMustSpillOver(
                Scenario6Fixture.BuildBothPreferenceRun(), Scenario6SpecConstants.HolidayDays)
            .ShouldBe(
                Scenario6SpecConstants.DayShiftsInWindow,
                "with both preference employees away every one of the 42 day shifts goes to somebody who never "
                + "asked for one");
    }

    /// <summary>
    /// The cross-month run models the in-period half only, and its numbers follow ITS window rather
    /// than the main run's: eight days from the period start, five of them contractual working days.
    /// A guard pinned to the main run's ten-and-four split would have passed S6a and mis-measured this.
    /// </summary>
    [Test]
    public void TheCrossMonthRunModelsOnlyTheInPeriodHalfOfItsWindow()
    {
        var definition = Scenario6Fixture.BuildCrossMonthRun();
        var rows = definition.Absences.Rows;

        rows.Count.ShouldBe(8, "1 to 8 March is eight days; the February half never reaches the engine");
        rows[0].Date.ShouldBe(AutofillSpecConstants.PeriodFrom);
        rows[^1].Date.ShouldBe(Scenario6SpecConstants.CrossMonthHolidayUntil);
        rows.Count(r => r.GrantsCredit).ShouldBe(5, "2 to 6 March are the working days of that window");

        var credit = (double)definition.Absences.CreditHoursOf(
            Scenario6SpecConstants.CrossMonthHolidayEmployee);
        credit.ShouldBe(
            5 * (double)Scenario6SpecConstants.DailyCreditHours,
            Scenario6SpecConstants.CreditTolerance);

        var carryIn = definition.OpenCarryIns.Single(c => string.Equals(
            c.AgentId, Scenario6SpecConstants.CrossMonthHolidayEmployee, StringComparison.Ordinal));
        for (var offset = 0; offset < carryIn.MissingDays; offset++)
        {
            definition.Absences
                .Covers(carryIn.AgentId, definition.PeriodFrom.AddDays(offset))
                .ShouldBeTrue(
                    "every day the carried-in package still owes must be an absence day, or the collision this run "
                    + "measures would only be partial");
        }

        TestContext.Out.WriteLine(
            $"S6d: the carried-in package of {carryIn.AgentId} still owes "
            + $"{carryIn.MissingDays.ToString(CultureInfo.InvariantCulture)} day(s) and the holiday takes all of "
            + $"them; credit {credit.ToString("0.##", CultureInfo.InvariantCulture)} h");
    }

    /// <summary>
    /// The calibration run really does make the five-work/two-free rhythm reachable again. Without
    /// that, its four rhythm assertions would ask for something the arithmetic forbids and would be red
    /// for a reason that has nothing to do with the algorithm.
    /// </summary>
    [Test]
    public void TheCalibrationRunBringsTheWorkShareBelowTheFiveTwoCeiling()
    {
        var main = AbsenceAnalyzer.BuildAvailability(Scenario6Fixture.BuildMainRun());
        var calibration = AbsenceAnalyzer.BuildAvailability(Scenario6Fixture.BuildCalibrationRun());

        main.WorkRatioRequired.ShouldBeGreaterThan(
            main.MaxRatioUnder52,
            "the main run is above the 5/2 ceiling — that is why it needs a calibration run at all");

        calibration.SlotsTotal.ShouldBe(544, "18 employees over 31 days minus the 14 absent days");
        calibration.WorkRatioRequired.ShouldBe(0.684, 0.001);
        calibration.WorkRatioRequired.ShouldBeLessThan(
            calibration.MaxRatioUnder52,
            "the eighteenth employee has to bring the work share below 71.43 %, or the rhythm stays unreachable");
        calibration.ForcedExtraShifts.ShouldBe(0, "below the ceiling nothing is forced");

        var eighteenth = Scenario6Fixture.BuildCalibrationRun().Context.Agents
            .Single(a => a.Id == Scenario6SpecConstants.EighteenthEmployee);
        eighteenth.GuaranteedHours.ShouldBe(Scenario6SpecConstants.EighteenthEmployeeGuaranteedHours);
    }

    /// <summary>
    /// The unsolvable run really is unsolvable, and provably so from the fixture: eleven employees for
    /// twelve daily assignments. If this ever came out solvable, S6-15 would be judging an ordinary run.
    /// </summary>
    [Test]
    public void TheUnsolvableRunIsProvablyUnsolvable()
    {
        var definition = Scenario6Fixture.BuildUnsolvableRun();
        var availability = AbsenceAnalyzer.BuildAvailability(definition);

        definition.Absences.Employees().Count.ShouldBe(Scenario6SpecConstants.UnsolvableRunEmployees.Count);
        definition.Absences.Rows.Count.ShouldBe(
            Scenario6SpecConstants.UnsolvableRunEmployees.Count * Scenario6SpecConstants.HolidayRows);

        var onTheFirstHolidayDay = definition.EmployeesInListOrder
            .Count(e => AbsenceAnalyzer.IsAvailableOn(e, Scenario6SpecConstants.HolidayFrom, definition));
        onTheFirstHolidayDay.ShouldBe(
            11,
            "on the first holiday day no FREE window overlaps, so exactly the eleven the specification names remain "
            + "for twelve assignments");

        availability.TightestDay.ShouldNotBeNull();
        availability.TightestDay!.RequiredAssignments.ShouldBe(Scenario5SpecConstants.AssignmentsPerDay);
        availability.TightestDay.AvailableEmployees.ShouldBe(
            10,
            "the tightest day is tighter still than the eleven the specification names: on 10 to 12 March the FREE "
            + "window of MA-01 lands inside the holiday of the other six, leaving ten. The specification's figure "
            + "counts the absences alone, which the line above pins; this line is the fixture's true minimum and it "
            + "only strengthens the infeasibility");
        availability.TightestDay.Ratio.ShouldBeGreaterThan(
            Scenario6SpecConstants.MaxTightestDayRatio,
            "eleven employees cannot staff twelve slots, and ten can staff them even less");

        foreach (var employee in Scenario6SpecConstants.UnsolvableRunEmployees)
        {
            definition.ShiftPreferences!.Preferences
                .Any(p => string.Equals(p.AgentId, employee, StringComparison.Ordinal))
                .ShouldBeFalse($"'{employee}' must carry no preference; the setting names six who carry none");
            definition.ScheduleCommands!
                .CommandsOn(employee, Scenario6SpecConstants.HolidayFrom)
                .ShouldBeEmpty($"'{employee}' must carry no keyword command");
        }
    }

    /// <summary>
    /// A blocker without hours still blocks its day. The engine's credit computation skips it —
    /// <c>TokenFitnessEvaluator.ComputeBreakHoursByAgent</c> continues on <c>Hours &lt;= 0</c> — but the
    /// blocking predicate <c>IsBlockedByBreak</c> never reads the hours at all, so the four weekend rows
    /// of every holiday window are blocking rows that pay nothing. The measurement has to see them the
    /// same way, or a Saturday in the middle of a holiday would look plannable.
    /// </summary>
    [Test]
    public void AnHourlessAbsenceRowStillBlocksItsDay()
    {
        var definition = Scenario6Fixture.BuildMainRun();
        var unpaid = definition.Absences.Rows.Where(r => !r.GrantsCredit).ToList();

        unpaid.ShouldNotBeEmpty("the two weekends of the window are the hourless rows");

        foreach (var row in unpaid)
        {
            definition.Absences.Covers(row.AgentId, row.Date).ShouldBeTrue(
                "an hourless row must still cover its day in the measurement");
            AbsenceAnalyzer.IsAvailableOn(row.AgentId, row.Date, definition).ShouldBeFalse(
                "an hourless row must still remove its day from the available capacity");
            definition.Context.BreakBlockers
                .Any(b => b.AgentId == row.AgentId && b.FromInclusive == row.Date)
                .ShouldBeTrue("an hourless row must still reach the engine as a blocker");
        }

        definition.Absences.CreditHoursOf(Scenario6SpecConstants.HolidayEmployee)
            .ShouldBe(
                Scenario6SpecConstants.WindowCreditHours,
                "the hourless rows must contribute nothing to the credit, exactly as the engine's skip does");
    }

    /// <summary>
    /// An absence day is not a free day, checked on the one function every free-day measure reads.
    /// The rule has to live in a single place, or it would hold in the histogram and fail at the edges.
    /// </summary>
    [Test]
    public void AnAbsenceDayIsNeverCountedAsAFreeDay()
    {
        var definition = Scenario6Fixture.BuildMainRun();
        var employee = Scenario6SpecConstants.HolidayEmployee;

        var gap = AbsenceAnalyzer.FreeDaysBetween(
            Scenario6SpecConstants.HolidayFrom.AddDays(-1),
            Scenario6SpecConstants.HolidayUntil.AddDays(1),
            employee,
            definition);
        gap.ShouldBeEmpty("a gap that consists only of absence days holds no free day at all");

        var edge = AbsenceAnalyzer.FreeEdgeDays(
            Scenario6SpecConstants.HolidayFrom, Scenario6SpecConstants.HolidayUntil, employee, definition);
        edge.ShouldBe(0, "a period edge made of absence days holds no free day either");

        var withOneRealFreeDay = AbsenceAnalyzer.FreeDaysBetween(
            Scenario6SpecConstants.HolidayFrom.AddDays(-2),
            Scenario6SpecConstants.HolidayUntil.AddDays(1),
            employee,
            definition);
        withOneRealFreeDay.Count.ShouldBe(
            1, "the one day before the window that carries no absence is the only free day of that gap");
    }

    /// <summary>
    /// Which runs the specification's densification band applies to, stated once so phase C does not
    /// have to guess. The band of about six forced extra shifts was derived from the 513 places of the
    /// main run; a run with a different capacity forces a different number for a purely arithmetic
    /// reason, and S6-7 reports it there instead of asserting it.
    /// </summary>
    [Test]
    public void TheDensificationBandAppliesOnlyWhereTheMainRunCapacityIsReproduced()
    {
        var runs = new (string Name, AutofillScenarioDefinition Definition)[]
        {
            ("S6a", Scenario6Fixture.BuildMainRun()),
            ("S6b", Scenario6Fixture.BuildCalibrationRun()),
            ("S6c", Scenario6Fixture.BuildTopRankRun()),
            ("S6d", Scenario6Fixture.BuildCrossMonthRun()),
            ("S6e", Scenario6Fixture.BuildKeywordCoveredRun()),
            ("S6f", Scenario6Fixture.BuildBothPreferenceRun()),
            ("S6g", Scenario6Fixture.BuildUnsolvableRun()),
        };

        var withBand = new List<string>();
        foreach (var (name, definition) in runs)
        {
            var availability = AbsenceAnalyzer.BuildAvailability(definition);
            TestContext.Out.WriteLine(
                $"{name}: {availability.SlotsTotal.ToString(CultureInfo.InvariantCulture)} places, "
                + $"{availability.ForcedExtraShifts.ToString(CultureInfo.InvariantCulture)} forced extra shift(s)");
            if (availability.SlotsTotal == Scenario6SpecConstants.MainRunSlotsTotal)
            {
                withBand.Add(name);
                Math.Abs(availability.ForcedExtraShifts - Scenario6SpecConstants.ExpectedForcedExtraShifts)
                    .ShouldBeLessThanOrEqualTo(
                        Scenario6SpecConstants.ForcedExtraShiftsTolerance,
                        $"{name} reproduces the main run's capacity, so it must land inside the band");
            }
        }

        withBand.ShouldBe(
            ["S6a", "S6c", "S6e"],
            "exactly the three runs with a single fourteen-day window reproduce the 513 places the specification "
            + "derived its band from; every other run is reported rather than asserted");
    }

    /// <summary>
    /// The matrix draws every state with a glyph of its own. Scenario 6 added two markers to a set
    /// that already used 'X' for a day with several shifts, and a collision there would not fail
    /// anything — it would quietly turn one picture into another.
    /// </summary>
    [Test]
    public void TheMatrixSymbolsArePairwiseDistinct()
    {
        AutofillShiftCatalog.ValidateMatrixSymbols().ShouldBeEmpty();
        AutofillShiftCatalog.AbsenceSymbol.ShouldBe('X', "the specification names X for a holiday day");
        AutofillShiftCatalog.FreeCommandSymbol.ShouldBe('f', "the specification names f for a FREE day");
        AutofillShiftCatalog.FreeSymbol.ShouldBe('.', "an ordinary free day stays the dot");
        AutofillShiftCatalog.SymbolOf(AutofillShiftKind.Day).ShouldBe('T', "a day shift stays T");
    }

    /// <summary>
    /// The input rejects what would silently measure nothing: a day outside the period, two rows on
    /// one date, negative hours and an employee the scenario does not have. The stacked-row case is the
    /// sharpest of them — the engine accumulates break hours per agent without keying on the date, so
    /// two rows on one day pay twice while blocking once.
    /// </summary>
    [Test]
    public void TheAbsenceInputRejectsRowsThatWouldMeasureNothing()
    {
        var context = Scenario6Fixture.BuildMainRun().Context;
        var employees = Scenario5SpecConstants.Employees;
        var outside = AutofillSpecConstants.PeriodFrom.AddDays(-1);

        var problems = new AutofillBreakBlockerInput(
        [
            new AutofillBreakBlocker("MA-01", outside, Scenario6SpecConstants.HolidayReason, 8m),
            new AutofillBreakBlocker("MA-02", Scenario6SpecConstants.HolidayFrom, Scenario6SpecConstants.HolidayReason, 8m),
            new AutofillBreakBlocker("MA-02", Scenario6SpecConstants.HolidayFrom, "Krankheit", 4m),
            new AutofillBreakBlocker("MA-03", Scenario6SpecConstants.HolidayFrom, Scenario6SpecConstants.HolidayReason, -1m),
            new AutofillBreakBlocker("MA-99", Scenario6SpecConstants.HolidayFrom, Scenario6SpecConstants.HolidayReason, 8m),
        ]).ValidationProblems(context, employees);

        TestContext.Out.WriteLine("rejections: " + string.Join(" | ", problems));

        problems.Count.ShouldBe(4, "one problem per mistake: outside the period, stacked, negative, unknown");
        problems.ShouldContain(p => p.Contains("outside the period", StringComparison.Ordinal));
        problems.ShouldContain(p => p.Contains("credited again", StringComparison.Ordinal));
        problems.ShouldContain(p => p.Contains("Negative", StringComparison.Ordinal));
        problems.ShouldContain(p => p.Contains("MA-99", StringComparison.Ordinal));
    }

    /// <summary>
    /// A missing control run must be reported as missing, not as agreement. This is the one failure
    /// mode of scenario 6 that would look like a pass: the artifact is gone, the comparison finds no
    /// difference because it read nothing, and every diff assertion turns green while measuring an
    /// empty set. The reader is pointed at a path that cannot exist and the state has to say so.
    /// </summary>
    [Test]
    public void TheComparisonReportsAMissingControlRunRatherThanNoDifference()
    {
        var previous = Environment.GetEnvironmentVariable(BaselineMetricsReader.BaselineOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                BaselineMetricsReader.BaselineOverrideVariable, NonexistentBaselinePath);

            var load = BaselineMetricsReader.Load(
                Scenario6SpecConstants.BaselineScenarioName,
                "Scenario-that-was-never-run",
                Scenario6SpecConstants.BaselineRunLabel);

            load.IsAvailable.ShouldBeFalse();
            load.Problem.ShouldNotBeNull();
            load.Source.ShouldContain(NonexistentBaselinePath);

            var definition = Scenario6Fixture.BuildMainRun();
            var diff = BaselineDiffAnalyzer.Build(
                load, EmptyMetricsFor(definition), definition, Scenario6SpecConstants.BaselineLabel);

            diff.Mode.ShouldBe(BaselineDiffMode.NotAvailable);
            diff.IsAvailable.ShouldBeFalse();
            diff.NightDiffs.ShouldBeEmpty(
                "a comparison that read nothing must report nothing, never zero differences");
            diff.Notes.ShouldNotBeEmpty("the reason has to travel with the state");

            TestContext.Out.WriteLine("missing-baseline state: " + Scenario6Diagnostics.DescribeDiff(diff));
        }
        finally
        {
            Environment.SetEnvironmentVariable(BaselineMetricsReader.BaselineOverrideVariable, previous);
        }
    }

    /// <summary>
    /// Reports whether the control run S6-K is available right now, and reads it when it is. It is
    /// INCONCLUSIVE and not red when the artifact is missing: the live artifact folder is git-ignored
    /// and is rewritten by every run of the same test, so its absence is a fact about this working copy
    /// and not a defect of the suite. Phase C either runs scenario 5a once or points the override
    /// variable at a secured copy.
    /// </summary>
    [Test]
    public void TheControlRunArtifactIsReadableOrTheReasonIsNamed()
    {
        var load = BaselineMetricsReader.Load(
            Scenario6SpecConstants.BaselineScenarioName,
            Scenario6SpecConstants.BaselineTestName,
            Scenario6SpecConstants.BaselineRunLabel);

        if (!load.IsAvailable)
        {
            Assert.Inconclusive(
                "The control run S6-K is not available in this working copy, so the scenario-6 comparisons cannot "
                + "run yet. This is expected on a fresh checkout: the artifact folder is git-ignored. " + load.Problem);
            return;
        }

        var snapshot = load.Snapshot!;
        TestContext.Out.WriteLine(
            $"control run read from '{load.Source}': {snapshot.TestName}.{snapshot.RunLabel}, "
            + $"{snapshot.ShiftTypeCountPerEmployee.Count.ToString(CultureInfo.InvariantCulture)} employees, "
            + $"{snapshot.Assignments.Count.ToString(CultureInfo.InvariantCulture)} assignment rows "
            + (snapshot.HasAssignments ? "(slot-level comparison possible)" : "(aggregate-only comparison)"));

        snapshot.TestName.ShouldBe(Scenario6SpecConstants.BaselineTestName);
        snapshot.ShiftTypeCountPerEmployee.Count.ShouldBe(
            Scenario5SpecConstants.EmployeeCount,
            "the control run is the scenario-5 main run and knows its seventeen employees");
        snapshot.NightsOf(Scenario6SpecConstants.HolidayEmployee).ShouldBeGreaterThanOrEqualTo(
            0, "reading the night count of the employee scenario 6 sends on holiday must work");
    }

    /// <summary>
    /// A measurement with nothing in it, for the missing-baseline test. It has to be a real
    /// <see cref="AutofillMetrics"/> because that is what the comparison takes, and building one from a
    /// plan would mean running the engine — which is exactly what this fast test must not do.
    /// </summary>
    /// <param name="definition">Scenario the empty measurement belongs to</param>
    private static AutofillMetrics EmptyMetricsFor(AutofillScenarioDefinition definition)
        => new(
            Scenario: Scenario6SpecConstants.ScenarioName,
            TestName: "not-run",
            RunLabel: "not-run",
            PeriodFrom: definition.PeriodFrom,
            PeriodUntil: definition.PeriodUntil,
            Coverage: new CoverageMetrics(0, 0, [], [], 0),
            Legality: new LegalityMetrics([], []),
            Packages: new PackageMetrics([], new Dictionary<string, int>(), new Dictionary<int, int>(), 0, 0, [], [], 0),
            Rotation: new RotationMetrics([], 0, 0, 0, 0),
            Hours: new HoursMetrics([], [], []),
            Fairness: new FairnessMetrics([], new ShiftTypeCountTriple(0, 0, 0), new ShiftTypeRatioTriple(0, 0, 0), [], 0),
            Eligibility: new EligibilityMetrics([], [], []),
            Keyword: new KeywordMetrics([]),
            CarryIn: [],
            Determinism: new DeterminismMetrics(RunsIdentical: true, FirstDifference: null),
            Fitness: new EngineFitness(0, 0, 0, 0, 0, 0),
            Notes: []);

}
