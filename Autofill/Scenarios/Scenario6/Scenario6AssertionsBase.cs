// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario5;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario6;

/// <summary>
/// The scenario-6 rules that every run of the scenario has to satisfy, whichever employee is on
/// holiday. Run-specific rules — the calibration figures of S6b, the redundancy of S6c, the month
/// seam of S6d, the pointless keyword of S6e, the full coverage of S6f, the infeasibility report of
/// S6g and the night comparison of S6a — live in the run's own fixture, because each of them is a
/// statement about ONE setup and would be vacuous everywhere else.
/// <para>
/// It inherits the scenario-5 assertions, which the specification keeps in force. A scenario-5
/// assertion that is red in the control run S6-K is red here for the same reason and is NOT a
/// scenario-6 finding — the report has to compare it against the S5 results before counting it.
/// </para>
/// <para>
/// THE ONE ASSERTION EXPECTED TO BE RED is S6-2. Owner decision O1 forbids work and a full-day
/// absence on the same calendar day however few hours reach into it, symmetrically at both edges. The
/// engine tests the START date of an assignment and nothing else, so a night shift beginning the
/// evening before a holiday and ending inside it passes every one of its gates. The assertion states
/// the RULE and carries the code evidence; it is not weakened to match the implementation.
/// </para>
/// </summary>
public abstract class Scenario6AssertionsBase : Scenario5AssertionsBase
{
    /// <summary>
    /// The code the S6-2 expectation is measured against, quoted so a reader can check the claim
    /// without opening the engine.
    /// </summary>
    protected const string StartDateOnlyEvidence =
        "Code evidence: Stage0HardConstraintChecker.cs:100-104 derives the date of an assignment from token.Date "
        + "alone and never from token.EndAt, and :164 tests exactly that date against IsBlockedByBreak "
        + "(:294-304, duplicated in SlotConstraintFilter.cs:658-669). CoreBreakBlocker.cs:17-22 carries no time "
        + "field at all. A shift that starts outside a window and ends inside it therefore passes every gate, "
        + "while owner decision O1 of 2026-08-14 forbids it. Fixing that is an engine stage of its own.";

    /// <summary>
    /// False for the run that is DESIGNED to be unsolvable. S6-8 asks whether the tightest day still
    /// leaves a solution, and asking it of a run built to have none would turn its whole point into a
    /// red line.
    /// </summary>
    protected virtual bool TightestDayMustBeSolvable => true;

    /// <summary>The absence measurement of this run.</summary>
    protected AbsenceMetrics Absences => Metrics.Absences;

    /// <summary>The capacity arithmetic of this run.</summary>
    protected AvailabilityMetrics Availability => Metrics.Availability;

    /// <summary>
    /// S6-1. A booked absence is absolute: no assignment of any kind may stand on an absence day. The
    /// block is a hard veto in seeding, in the auction, in every mutation and repair operator and in
    /// the theoretical maximum alike, so an entry here is a bypassed veto and never a trade-off.
    /// </summary>
    [Test]
    public void S6_1_NoAssignmentStandsOnAnAbsenceDay()
    {
        Absences.Rows.ShouldBeGreaterThan(
            0, "S6-1 needs a booked absence to judge; this run declares none.");

        Absences.Violations.ShouldBeEmpty(
            "S6-1: an absence is absolute. Every gate that can create a token consults the blocker list — "
            + "SlotConstraintFilter at seeding, Stage0HardConstraintChecker in the auction, both of them again "
            + "through every mutation and repair operator — and MaxPossibleCalculator removes the day from the "
            + "theoretical maximum. There is no soft term anywhere that could price a violation, so an assignment on "
            + "an absence day is a veto that was bypassed. Found: "
            + Scenario6Diagnostics.DescribeViolations(Absences.Violations));
    }

    /// <summary>
    /// S6-2. The owner's overlap rule: work and a full-day absence exclude each other on the same
    /// calendar day, however few hours reach into it, and symmetrically at both edges — a night shift
    /// beginning at 23:00 on the eve of a holiday is as forbidden as one beginning on its last day.
    /// <para>
    /// EXPECTED RED. The engine implements one edge only. The assertion states the rule as the owner
    /// decided it and carries the evidence; weakening it to the implemented half would turn a known
    /// engine gap into a green line.
    /// </para>
    /// </summary>
    [Test]
    [Category("SpecFirstRed")]
    public void S6_2_NoAssignmentReachesIntoAnAbsenceWindowFromOutside()
    {
        var reachingIn = Absences.BoundaryCases
            .Where(c => c.EvaluatedAgainst == AbsenceEdgeEvaluation.End)
            .ToList();

        if (reachingIn.Count == 0)
        {
            var onTheEves = ShiftsOnWindowEves();
            TestContext.Out.WriteLine(
                "S6-2: shifts standing on a window eve in this run: "
                + (onTheEves.Count == 0 ? "none" : string.Join(", ", onTheEves)));

            Assert.Inconclusive(
                "S6-2 could not be judged: no assignment reaches into an absence window from outside, but neither "
                + "did the situation arise. The gap the owner rule names can only show as a NIGHT shift on the day "
                + "before a window begins — that is the only span in the suite that starts on one calendar day and "
                + "ends on the next. This run placed "
                + (onTheEves.Count == 0
                    ? "no shift at all on any window eve"
                    : "only these shifts on a window eve: " + string.Join(", ", onTheEves))
                + ", so an empty result proves nothing about the engine. It is reported as inconclusive rather than "
                + "green, because a green spec-first-red assertion reads as 'the gap is closed' and this one would "
                + "mean 'the case never came up'. " + StartDateOnlyEvidence);
            return;
        }

        reachingIn.ShouldBeEmpty(
            "S6-2: owner decision O1 of 2026-08-14 — work and a full-day absence on the same calendar day exclude "
            + "each other regardless of how many hours reach into it, and the rule is symmetric: a night shift that "
            + "begins at 23:00 on the day before a holiday and ends at 07:00 inside it is forbidden, exactly like one "
            + "that begins on the last day of the holiday. "
            + StartDateOnlyEvidence
            + " Assignments reaching into a window from outside: "
            + Scenario6Diagnostics.DescribeBoundaryCases(reachingIn));
    }

    /// <summary>
    /// S6-2, the consistency half — and NOT spec-first red. Whatever the rule is, the plan and the
    /// measurement have to use ONE rule: everything the engine's own half forbids must be absent from
    /// the plan, and the measurement must grade both edges by the same standard instead of quietly
    /// being lenient about the one the engine cannot see.
    /// </summary>
    [Test]
    public void S6_2_TheAssigningRuleAndTheCheckingRuleAreTheSameRule()
    {
        var problems = new List<string>();

        var startsInside = Absences.BoundaryCases
            .Where(c => c.EvaluatedAgainst is AbsenceEdgeEvaluation.Start or AbsenceEdgeEvaluation.Both)
            .ToList();
        if (startsInside.Count > 0)
        {
            problems.Add(
                $"{startsInside.Count.ToString(CultureInfo.InvariantCulture)} assignment(s) start inside a window, "
                + "which is the half the engine itself forbids: "
                + Scenario6Diagnostics.DescribeBoundaryCases(startsInside));
        }

        var ungraded = Absences.BoundaryCases.Where(c => !c.ViolatesOverlapRule).ToList();
        if (ungraded.Count > 0)
        {
            problems.Add(
                $"{ungraded.Count.ToString(CultureInfo.InvariantCulture)} boundary case(s) touch a window but are "
                + "not graded as breaking the overlap rule, so the measurement treats the two edges differently");
        }

        problems.ShouldBeEmpty(
            "S6-2 consistency: assignment and check must rest on one rule. The engine's half — an assignment whose "
            + "own day is an absence day — must never appear in a plan, and the measurement must grade an overlap as "
            + "an overlap at either edge. This assertion is deliberately NOT spec-first red: it asks only for "
            + "consistency, which holds today, while the rule gap itself is S6-2 above. "
            + Describe(problems));
    }

    /// <summary>
    /// S6-3. No assignment on the first or the last day of an absence window. Both days lie wholly
    /// inside it, so today's start-date test already blocks them — this is the half of the boundary
    /// question the engine does implement, and it is asserted plainly and without a spec-first marker.
    /// </summary>
    [Test]
    public void S6_3_TheFirstAndLastDayOfEveryWindowStayEmpty()
    {
        var edges = Metrics.Absences.Entries
            .SelectMany(e => new[] { (e.Employee, Date: e.FromInclusive), (e.Employee, Date: e.UntilInclusive) })
            .ToList();

        var occupied = Absences.Violations
            .Where(v => edges.Any(e => string.Equals(e.Employee, v.Employee, StringComparison.Ordinal)
                                       && e.Date == v.Date))
            .ToList();

        occupied.ShouldBeEmpty(
            "S6-3: the first and the last day of an absence window lie wholly inside it, so an assignment starting on "
            + "either of them is blocked by the engine's own start-date test. A violation here is therefore not the "
            + "known boundary gap of S6-2 but a failure of the mechanism the engine does implement. Found: "
            + Scenario6Diagnostics.DescribeViolations(occupied));
    }

    /// <summary>
    /// S6-4. An absence day is not a free day. It is blocked by an input rather than left empty by the
    /// plan and it pays hours, so it must be drawn as its own symbol in the matrix and must not appear
    /// in the free-block histogram, in the ideal share or in the free edges.
    /// </summary>
    [Test]
    public void S6_4_AbsenceDaysAreNotCountedAsFreeDays()
    {
        var problems = new List<string>();

        if (Absences.DaysCountedAsFree)
        {
            problems.Add(
                "at least one absence day was counted as a free day in the package measures");
        }

        if (AutofillShiftCatalog.AbsenceSymbol == AutofillShiftCatalog.FreeSymbol)
        {
            problems.Add(
                $"the matrix draws an absence day and a free day with the same symbol "
                + $"'{AutofillShiftCatalog.AbsenceSymbol}'");
        }

        var longestWindow = Absences.Entries.Count == 0 ? 0 : Absences.Entries.Max(e => e.Rows);
        var leaked = Metrics.Packages.FreeBlockHistogram
            .Where(entry => entry.Value > 0 && entry.Key >= longestWindow)
            .ToList();
        if (longestWindow > 0 && leaked.Count > 0)
        {
            problems.Add(
                $"the free-block histogram holds {leaked.Count.ToString(CultureInfo.InvariantCulture)} block(s) of "
                + $"at least {longestWindow.ToString(CultureInfo.InvariantCulture)} days, the length of the longest "
                + "absence window — a block that long can only exist if the window leaked into it: "
                + string.Join(
                    ", ",
                    leaked.Select(e =>
                        $"{e.Key.ToString(CultureInfo.InvariantCulture)}d x"
                        + e.Value.ToString(CultureInfo.InvariantCulture))));
        }

        foreach (var employee in Absences.Entries.Select(e => e.Employee).Distinct(StringComparer.Ordinal))
        {
            var absentDays = Definition.Absences.DaysOf(employee).Count;
            var edge = Metrics.Packages.FreeEdges
                .FirstOrDefault(e => string.Equals(e.Employee, employee, StringComparison.Ordinal));
            if (edge is not null && edge.LeadingFreeDays + edge.TrailingFreeDays + absentDays
                > AutofillSpecConstants.PeriodDays)
            {
                problems.Add(
                    $"the free edges of '{employee}' ({edge.LeadingFreeDays.ToString(CultureInfo.InvariantCulture)} "
                    + $"leading, {edge.TrailingFreeDays.ToString(CultureInfo.InvariantCulture)} trailing) plus his "
                    + $"{absentDays.ToString(CultureInfo.InvariantCulture)} absence days exceed the period, so at "
                    + "least one absence day was counted twice — once as absent and once as a free edge day");
            }
        }

        problems.ShouldBeEmpty(
            "S6-4: an absence day and a free day are different states. The free day is what the plan left over; the "
            + "absence day is closed by an input and pays hours towards the target. Counting the second as the first "
            + "would make a holiday look like a generous rhythm in packages.freeBlockHistogram and would reward a "
            + $"plan for a rest it never granted. The matrix draws it as '{AutofillShiftCatalog.AbsenceSymbol}'. "
            + Describe(problems));
    }

    /// <summary>
    /// S6-5. What the rotation did across the long absence — MEASURED and reported, never asserted
    /// into a direction. The discovery of 2026-08-14 found no documented rotation reset after an
    /// absence anywhere in the engine and no owner decision names one, so asserting either
    /// "continues forwards" or "restarts on early" would invent a specification. What IS asserted is
    /// that the measurement exists: a window with packages on both sides must produce an entry, or the
    /// report would be silent about the question it was built to answer.
    /// </summary>
    [Test]
    public void S6_5_TheRotationAcrossTheAbsenceIsMeasured()
    {
        var entries = Metrics.Rotation.ContinuityAcrossAbsence;

        TestContext.Out.WriteLine("S6-5 continuity across the absence: " + Scenario6Diagnostics.DescribeContinuity(entries));

        entries.Count.ShouldBe(
            Absences.Entries.Count,
            "S6-5: every absence window needs one continuity entry, whether or not it has packages on both sides. "
            + "The direction itself is a finding and not a rule — no reset is documented in the engine and none was "
            + "decided — so this assertion only guarantees the question is answered in the artifact.");
    }

    /// <summary>
    /// S6-6. The hour target is not shortened by an absence; the absence hours arrive as a CREDIT on
    /// the actual side. Every absent employee must therefore reach his unchanged target once the
    /// credit is counted — or state the shortfall as forced with a cause. A silent miss is the failure.
    /// </summary>
    [Test]
    public void S6_6_TheAbsenceCreditCountsTowardsTheUnchangedTarget()
    {
        var entries = Metrics.Hours.CreditTarget;
        TestContext.Out.WriteLine("S6-6 credit target: " + Scenario6Diagnostics.DescribeCreditTarget(entries));

        entries.Count.ShouldBe(
            Absences.Entries.Select(e => e.Employee).Distinct(StringComparer.Ordinal).Count(),
            "S6-6 needs one credit-target entry per absent employee.");

        var silent = entries
            .Where(e => !e.Fulfilled && !e.ForcedShortfallDeclared)
            .ToList();

        silent.ShouldBeEmpty(
            "S6-6: an absence does not reduce the hour target. TokenFitnessEvaluator.cs:222-225 computes "
            + "covered = CurrentHours + tokenHours + breakHours and compares it against the UNCHANGED "
            + "GuaranteedHours, so the holiday hours are a credit and the target stays 180 h. An absent employee "
            + "must therefore reach planned + credit >= target, and a shortfall is acceptable only when it is "
            + "declared forced with a cause — that is, when even the most optimistic rhythm over the days he has "
            + "left cannot close the gap. A shortfall that is neither reached nor declared is a silent failure of "
            + "the target. BEFORE READING A RED HERE AS AN ENGINE DEFECT, look at the 'reachability of ...' line "
            + "this run logged before it started: in the main run the rhythm allows at most thirteen further shifts "
            + "and about thirteen are needed, so the target is reachable only by a perfect pack. A shortfall of one "
            + "or two shifts is then the fixture leaving no slack, and it will show here as NOT declared — because "
            + "the optimistic bound says it was possible — while the honest reading is that nothing but a perfect "
            + "plan could have made it. Found: " + Scenario6Diagnostics.DescribeCreditTarget(silent));
    }

    /// <summary>
    /// S6-7. The work the absence removes has to be carried by the others, and that densification is
    /// both bounded and localised: bounded by the arithmetic of the fixture — about six extra shifts
    /// beyond what a clean five-work/two-free rhythm carries — and localised inside the absence
    /// window, where the missing capacity actually is.
    /// </summary>
    [Test]
    public void S6_7_TheDensificationIsBoundedAndSitsInsideTheWindow()
    {
        var problems = new List<string>();
        var expected = Scenario6SpecConstants.ExpectedForcedExtraShifts;
        var tolerance = Scenario6SpecConstants.ForcedExtraShiftsTolerance;
        var bandApplies = Availability.SlotsTotal == Scenario6SpecConstants.MainRunSlotsTotal;

        TestContext.Out.WriteLine(
            $"S6-7 densification: {Availability.ForcedExtraShifts.ToString(CultureInfo.InvariantCulture)} extra "
            + $"shift(s) beyond a clean 5/2 rhythm over {Availability.SlotsTotal.ToString(CultureInfo.InvariantCulture)} "
            + "places; the specification's band of "
            + $"{expected.ToString(CultureInfo.InvariantCulture)} +/- {tolerance.ToString(CultureInfo.InvariantCulture)} "
            + (bandApplies ? "applies to this run" : "is NOT applied — this run has a different capacity"));

        if (bandApplies && Math.Abs(Availability.ForcedExtraShifts - expected) > tolerance)
        {
            problems.Add(
                $"the fixture forces {Availability.ForcedExtraShifts.ToString(CultureInfo.InvariantCulture)} extra "
                + $"shifts beyond a clean 5/2 rhythm, outside the band "
                + $"{(expected - tolerance).ToString(CultureInfo.InvariantCulture)}.."
                + (expected + tolerance).ToString(CultureInfo.InvariantCulture));
        }

        var extensions = Metrics.Packages.ForcedExtensions;
        var shortened = Metrics.Packages.ShortenedFreeBlocks;
        var events = extensions.Count + shortened.Count;
        var inWindow = extensions.Count(e => e.TouchesAbsenceWindow)
            + shortened.Count(s => s.TouchesAbsenceWindow);

        TestContext.Out.WriteLine(
            $"S6-7 surplus in the plan: {extensions.Count.ToString(CultureInfo.InvariantCulture)} package(s) over "
            + $"{AutofillSpecConstants.MaxWorkDays.ToString(CultureInfo.InvariantCulture)} days and "
            + $"{shortened.Count.ToString(CultureInfo.InvariantCulture)} free block(s) under "
            + $"{AutofillSpecConstants.MinRestDays.ToString(CultureInfo.InvariantCulture)} days; "
            + $"{inWindow.ToString(CultureInfo.InvariantCulture)} of them touch an absence window");

        if (Availability.ForcedExtraShifts > 0 && events == 0)
        {
            problems.Add(
                $"the fixture forces {Availability.ForcedExtraShifts.ToString(CultureInfo.InvariantCulture)} "
                + "assignment(s) beyond what a clean 5/2 rhythm carries, but the plan holds neither an over-long "
                + "package nor a shortened free block — the surplus has to become visible as one of the two, so "
                + "either the plan is not what the coverage metric says it is or the package measure is not seeing it");
        }

        var windowShare = Definition.Absences.Rows.Count == 0
            ? 0
            : Definition.Absences.AllWindows().Sum(w => w.Until.DayNumber - w.From.DayNumber + 1)
                / (double)AutofillSpecConstants.PeriodDays;
        var eventShare = events == 0 ? 0 : inWindow / (double)events;
        if (events > 0 && eventShare < windowShare)
        {
            problems.Add(
                "only " + (eventShare * 100).ToString("0.0", CultureInfo.InvariantCulture)
                + " % of the over-long packages and shortened free blocks touch an absence window, while the windows "
                + "cover " + (windowShare * 100).ToString("0.0", CultureInfo.InvariantCulture)
                + " % of the period — the plan spread the cost of the absence over the whole month instead of "
                + "absorbing it where the capacity is missing");
        }

        problems.ShouldBeEmpty(
            "S6-7: the absence removes capacity in a fixed window, so the plan has to work harder exactly there and "
            + "only there. The bound comes from the fixture and is computed before the run — it is an arithmetic "
            + "property of the roster, never a verdict about the algorithm — and the localisation is what separates "
            + "a plan that absorbed the absence from one that spread its cost over the whole month. The "
            + "specification derived its band of about six extra shifts from the 513 places of the main run, so the "
            + "band is asserted only where that capacity is reproduced and is REPORTED everywhere else: the "
            + "calibration run has capacity to spare and forces nothing, the run with two absentees loses more "
            + "capacity and forces more, and holding all of them to one literal would turn arithmetic into a red "
            + "line. The localisation half applies to every run. "
            + Scenario6Diagnostics.DescribeAvailability(Availability) + ". " + Describe(problems));
    }

    /// <summary>
    /// S6-8. The tightest day of the period must still leave a solution: it may not demand more
    /// assignments than it has available employees. This is a property of the fixture and is checked
    /// before anything is concluded about the plan.
    /// </summary>
    [Test]
    public void S6_8_TheTightestDayStillLeavesASolution()
    {
        var tightest = Availability.TightestDay;
        TestContext.Out.WriteLine("S6-8 tightest day: " + Scenario6Diagnostics.DescribeTightestDay(tightest));

        if (!TightestDayMustBeSolvable)
        {
            Assert.Pass(
                "S6-8 does not apply to this run: it is built to be unsolvable, and the infeasibility is the "
                + "measurement. See the run's own assertion. Measured: "
                + Scenario6Diagnostics.DescribeTightestDay(tightest));
            return;
        }

        tightest.ShouldNotBeNull("S6-8: the tightest day was not computed.");
        tightest!.Ratio.ShouldBeLessThanOrEqualTo(
            Scenario6SpecConstants.MaxTightestDayRatio,
            "S6-8: on the tightest day of the period the demanded assignments must not outnumber the employees an "
            + "absence or a FREE command leaves available. Above that ratio the day is unstaffable whatever the "
            + "algorithm does, and every other assertion about this run would be judging an impossible task. "
            + Scenario6Diagnostics.DescribeTightestDay(tightest));
    }

    /// <summary>
    /// S6-9. A preference stays a preference. Inside the absence window the day shifts far outnumber
    /// what the remaining preferring employees can work under the contractual rhythm, so a large share
    /// of them necessarily goes to employees who never asked for one. That is arithmetic and not a
    /// violation — the assertion pins the arithmetic so the report cannot read the spill-over as a
    /// broken preference.
    /// </summary>
    [Test]
    public void S6_9_ThePreferenceStaysAPreferenceInsideTheWindow()
    {
        var windowDays = Absences.Entries
            .SelectMany(e => DaysOf(e.FromInclusive, e.UntilInclusive))
            .Distinct()
            .ToHashSet();
        if (windowDays.Count == 0)
        {
            Assert.Pass("S6-9 needs an absence window; this run declares none.");
            return;
        }

        var inWindow = Metrics.DayShift.Assignments
            .Where(a => windowDays.Contains(a.Date))
            .ToList();
        var toNonPreferring = inWindow.Count(a => !a.ViaPreference);
        var expectedFloor = Scenario6SpecConstants.DayShiftsThatMustSpillOver(Definition, windowDays.Count);

        TestContext.Out.WriteLine(
            $"S6-9: {inWindow.Count.ToString(CultureInfo.InvariantCulture)} day shift(s) inside the window, "
            + $"{toNonPreferring.ToString(CultureInfo.InvariantCulture)} of them to employees without a preference; "
            + $"the arithmetic floor is {expectedFloor.ToString(CultureInfo.InvariantCulture)}");

        toNonPreferring.ShouldBeGreaterThanOrEqualTo(
            expectedFloor,
            "S6-9: the day shifts inside the absence window outnumber what the preferring employees still present "
            + "can work under the five-work/two-free rhythm, so at least the floor above must go to employees who "
            + "never asked for one. A count BELOW the floor would mean the plan handed preferring employees more day "
            + "shifts than the rhythm allows, which is a rule break dressed as preference satisfaction. The floor is "
            + "derived from the window length and the preferring employees still available, never from a literal.");
    }

    /// <summary>
    /// S6-20. The run reproduces itself and leaves the fixed previous month untouched. Everything else
    /// measured here is worthless without it: a plan that differs from itself makes every other number
    /// a coincidence.
    /// </summary>
    [Test]
    public void S6_20_TheRunIsDeterministic()
    {
        var problems = new List<string>();
        if (!Run.RunsIdentical)
        {
            problems.Add("the two runs differ at " + Run.FirstDifference);
        }

        if (!Run.CarryInUnchanged)
        {
            problems.Add("the fixed previous month changed at " + Run.CarryInDifference);
        }

        problems.ShouldBeEmpty(
            "S6-20: two runs with the same input and the same seed must produce the same plan, and the fixed "
            + "previous month must be byte-identical afterwards. Absences are a new input class and the first one "
            + "that touches the theoretical maximum, the seeding filter and the hour credit at once, so the "
            + "determinism proof is repeated for every scenario-6 run rather than inherited. " + Describe(problems));
    }

    /// <summary>
    /// Every shift an absent employee holds on the day directly before one of his absence windows —
    /// the only place the boundary gap of S6-2 can appear. It is read off the plan and not off the
    /// boundary cases, because the point is to tell "no shift stood there" apart from "a shift stood
    /// there and did not reach in".
    /// </summary>
    protected IReadOnlyList<string> ShiftsOnWindowEves()
    {
        var eves = Absences.Entries
            .Select(e => (e.Employee, Eve: e.FromInclusive.AddDays(-1)))
            .ToList();

        return Run.Plan.Tokens
            .Where(t => eves.Any(e => string.Equals(e.Employee, t.AgentId, StringComparison.Ordinal)
                                      && e.Eve == t.Date))
            .Select(t =>
                $"{t.AgentId} {t.Date:yyyy-MM-dd} {AutofillShiftCatalog.SlotKindOf(t.ShiftRefId)?.ToString() ?? "?"} "
                + $"ends {t.EndAt:MM-dd HH:mm}")
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>All calendar days of an inclusive range.</summary>
    /// <param name="from">First day</param>
    /// <param name="until">Last day, inclusive</param>
    protected static IEnumerable<DateOnly> DaysOf(DateOnly from, DateOnly until)
    {
        for (var date = from; date <= until; date = date.AddDays(1))
        {
            yield return date;
        }
    }
}
