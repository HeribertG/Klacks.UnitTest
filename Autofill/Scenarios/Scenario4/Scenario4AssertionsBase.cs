// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Analysis;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Support;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario4;

/// <summary>
/// Assertions A1 to A14 of specification part 1, evaluated over all three orders together, plus the
/// scenario-4 assertions S4-1 to S4-16 that only a multi-order plan can pose. Shared by every
/// scenario-4 run so the phase-D table compares the same judgements across the runs.
/// <para>
/// Two of the scenario-4 assertions deliberately do not assert. S4-5 and S4-6 ask about order loyalty,
/// and phase A' established that no mechanism in the engine keeps an employee on one order: the only
/// candidate term reads a location field that every producing site sets to null. Asserting a rule that
/// provably has no implementation would only restate the discovery; the runs therefore MEASURE the two
/// and report the numbers, and the owner decides afterwards whether they become a rule.
/// </para>
/// <para>
/// The references A6 and A8 read are the scenario-4 references, not the ones of scenarios 1 to 3. The
/// shared constant for the top ranks describes a roster of five and doubles as the definition of the
/// existing baseline band pins, so touching it would silently move other scenarios' floors.
/// </para>
/// <para>
/// A15 is not implemented here either, for the reason given in decision 5 of tests/autofill/SPEC.md:
/// the recovery engine repairs an absence and does not replan a month. The second entry point scenario
/// 4 does test is the frozen-prefix replanning of run S4e, which is a different mechanism and carries
/// its own assertions.
/// </para>
/// </summary>
public abstract class Scenario4AssertionsBase
{
    /// <summary>Separator between the individual problem lines of one assertion message.</summary>
    protected const string ProblemSeparator = " | ";

    private const double HoursComparisonEpsilon = 1e-9;

    private const double ShareComparisonEpsilon = 1e-9;

    /// <summary>The scenario of the run the assertions read; guards its own fixture validity.</summary>
    protected abstract AutofillScenarioDefinition Definition { get; }

    /// <summary>The cached run of the fixture; guards its own fixture validity.</summary>
    protected abstract DeterministicRunResult Run { get; }

    /// <summary>Measurement of run 1 — the plan every assertion judges.</summary>
    protected AutofillMetrics Metrics => Run.Metrics;

    [Test]
    public void A1_S4_1_EveryShiftOfEveryOrderIsStaffed()
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
                + Scenario4Diagnostics.DescribeOrderCoverage(starved));
        }

        problems.ShouldBeEmpty(
            $"A1/S4-1: all {Scenario4SpecConstants.TotalRequiredShifts.ToString(CultureInfo.InvariantCulture)} demanded "
            + $"shifts of {Definition.PeriodFrom:yyyy-MM-dd}..{Definition.PeriodUntil:yyyy-MM-dd} must be staffed, and "
            + "no single order may be systematically worse off than the others. Coverage per order: "
            + $"{Scenario4Diagnostics.DescribeOrderCoverage(coverage.PerOrder)}. {Describe(problems)}");
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
                + "assignments");
        }

        if (coverage.CrossOrderDoubleBookings.Count > 0)
        {
            problems.Add(
                $"{coverage.CrossOrderDoubleBookings.Count.ToString(CultureInfo.InvariantCulture)} day(s) on which one "
                + "employee holds shifts of more than one order: "
                + Scenario4Diagnostics.DescribeCrossOrderDays(coverage.CrossOrderDoubleBookings));
        }

        if (coverage.OversuppliedSlots > 0)
        {
            problems.Add(
                $"{coverage.OversuppliedSlots.ToString(CultureInfo.InvariantCulture)} slot(s) received more than one "
                + "employee");
        }

        problems.ShouldBeEmpty(
            "A2/S4-2: no shift may be staffed twice and no employee may hold two shifts at once, across order "
            + "boundaries included. Note on the wording of the specification: its clause 'no employee in two shifts a "
            + "day' was struck by the owner correction of 2026-08-08, so two different non-overlapping shifts on one "
            + "day are not a conflict here; with a rest time of 11 h they cannot occur anyway. " + Describe(problems));
    }

    [Test]
    public void A3_S4_4_RestTimeAndNightToEarlyAreRespectedAcrossOrders()
    {
        var legality = Metrics.Legality;
        var problems = new List<string>();

        if (legality.NightToEarlyViolations.Count > 0)
        {
            problems.Add(
                $"{legality.NightToEarlyViolations.Count.ToString(CultureInfo.InvariantCulture)} night-to-early "
                + "transition(s)");
        }

        if (legality.RestViolations.Count > 0)
        {
            problems.Add(
                $"{legality.RestViolations.Count.ToString(CultureInfo.InvariantCulture)} shift pair(s) below the "
                + $"contractual rest time of {AutofillSpecConstants.MinRestHours.ToString(CultureInfo.InvariantCulture)} h");
        }

        if (legality.RestViolationsCrossOrder.Count > 0)
        {
            problems.Add(
                "of those, across order boundaries: "
                + Scenario4Diagnostics.DescribeCrossOrderRest(legality.RestViolationsCrossOrder));
        }

        problems.ShouldBeEmpty(
            "A3/S4-4: the rest time must hold for the employee as a whole, so a shift of one order and a shift of "
            + "another count against each other exactly like two shifts of the same order, and a night shift must "
            + "never be followed by an early shift on the next day. The night runs 23:00 to 07:00 of the following "
            + "day, so the seam is part of the check. " + Describe(problems));
    }

    [Test]
    public void S4_3_EveryDayCarriesNineAssignments()
    {
        var deviating = Metrics.Coverage.AssignmentsPerDay
            .Where(d => d.Count != Scenario4SpecConstants.AssignmentsPerDay)
            .ToList();

        deviating.ShouldBeEmpty(
            $"S4-3: every one of the {AutofillSpecConstants.PeriodDays.ToString(CultureInfo.InvariantCulture)} days "
            + $"must carry exactly {Scenario4SpecConstants.AssignmentsPerDay.ToString(CultureInfo.InvariantCulture)} "
            + "assignments — three orders with three shifts each — but these days do not: "
            + Scenario4Diagnostics.DescribeDayCounts(deviating));
    }

    [Test]
    public void A4_ShiftKindStaysConstantInsideAPackage()
    {
        Metrics.Packages.MixedTypeCount.ShouldBe(
            0,
            "A4: the shift kind must stay constant inside a package and may only change at a package border, but "
            + $"{Metrics.Packages.MixedTypeCount.ToString(CultureInfo.InvariantCulture)} of "
            + $"{Metrics.Packages.Items.Count.ToString(CultureInfo.InvariantCulture)} package(s) mix kinds.");
    }

    [Test]
    public void A5_PackageLengthsFollowTheFiveTwoIdeal()
    {
        var packages = Metrics.Packages;
        var modeBucket = AutofillSpecConstants.ExpectedPackageLengthMode.ToString(CultureInfo.InvariantCulture);
        var problems = new List<string>();

        packages.LengthHistogram.TryGetValue(modeBucket, out var countAtMode);
        if (packages.Items.Count == 0)
        {
            problems.Add($"the plan contains no package at all, so it cannot peak at {modeBucket} days");
        }
        else
        {
            var atLeastAsFrequent = packages.LengthHistogram
                .Where(entry => !string.Equals(entry.Key, modeBucket, StringComparison.Ordinal))
                .Where(entry => entry.Value >= countAtMode)
                .Select(entry => $"{entry.Key} days:{entry.Value.ToString(CultureInfo.InvariantCulture)}")
                .ToList();
            if (atLeastAsFrequent.Count > 0)
            {
                problems.Add(
                    $"length {modeBucket} holds {countAtMode.ToString(CultureInfo.InvariantCulture)} package(s) and is "
                    + "not the single most frequent length; at least as frequent: "
                    + string.Join(", ", atLeastAsFrequent));
            }
        }

        var shortShareLimit = AutofillSpecConstants.ShortPackageShareLimit
            + AutofillSpecConstants.ShortPackageShareTolerance;
        var shortShare = AutofillPlanAnalyzer.ShortPackageShare(
            packages, AutofillSpecConstants.ShortPackageMaxLength);
        if (shortShare > shortShareLimit + ShareComparisonEpsilon)
        {
            problems.Add(
                $"{(shortShare * 100).ToString("0.#", CultureInfo.InvariantCulture)} % of the packages are at most "
                + $"{AutofillSpecConstants.ShortPackageMaxLength.ToString(CultureInfo.InvariantCulture)} days long, "
                + $"above the limit of {(shortShareLimit * 100).ToString("0.#", CultureInfo.InvariantCulture)} %");
        }

        var tooLong = AutofillPlanAnalyzer.PackagesLongerThan(packages, AutofillSpecConstants.MaxAllowedPackageLength);
        if (tooLong > 0)
        {
            problems.Add(
                $"{tooLong.ToString(CultureInfo.InvariantCulture)} package(s) exceed the maximum length of "
                + $"{AutofillSpecConstants.MaxAllowedPackageLength.ToString(CultureInfo.InvariantCulture)} days");
        }

        problems.ShouldBeEmpty(
            $"A5: package lengths must peak at {modeBucket} days and never exceed "
            + $"{AutofillSpecConstants.MaxAllowedPackageLength.ToString(CultureInfo.InvariantCulture)}. Histogram: "
            + $"{Scenario4Diagnostics.DescribeHistogram(packages.LengthHistogram)}. Free blocks: "
            + $"{Scenario4Diagnostics.DescribeFreeBlocks(packages.FreeBlockHistogram)}. A work share of 60 % implies a "
            + $"cycle of {Scenario4SpecConstants.RhythmPackageLength.ToString(CultureInfo.InvariantCulture)} work days "
            + $"and {Scenario4SpecConstants.RhythmFreeBlockLow.ToString(CultureInfo.InvariantCulture)} to "
            + $"{Scenario4SpecConstants.RhythmFreeBlockHigh.ToString(CultureInfo.InvariantCulture)} free days, so free "
            + "blocks longer than two are expected here and are not an A5 failure. Packages crossing the month "
            + "boundary count their February days. " + Describe(problems));
    }

    [Test]
    public void A6_S4_10_GuaranteedHoursAreServedTopDown()
    {
        var hours = Metrics.Hours;
        var threshold = Scenario4SpecConstants.ReferenceHoursTopRanks * AutofillSpecConstants.FulfilmentThreshold;
        var problems = new List<string>();

        if (hours.MonotonicityViolations.Count > 0)
        {
            problems.Add(
                "the fulfilment rises against the list order at "
                + Scenario4Diagnostics.DescribeMonotonicity(hours.MonotonicityViolations));
        }

        var belowReference = hours.PerEmployee
            .Where(e => e.ListRank <= Scenario4SpecConstants.TopRankUpperBound)
            .Where(e => e.PlannedHours + HoursComparisonEpsilon < threshold)
            .ToList();
        if (belowReference.Count > 0)
        {
            problems.Add(
                $"ranks 1 to {Scenario4SpecConstants.TopRankUpperBound.ToString(CultureInfo.InvariantCulture)} must "
                + $"each reach {threshold.ToString("0.#", CultureInfo.InvariantCulture)} h, but "
                + Scenario4Diagnostics.DescribeHours(belowReference) + " stay below");
        }

        problems.ShouldBeEmpty(
            "A6/S4-10: the guaranteed hours must be served in list order over all fifteen ranks, so the fulfilment "
            + "never rises going down the list and the top ranks reach the top-down reference. The reference is the "
            + "top-down resolution of the scenario: ranks 1 to "
            + $"{Scenario4SpecConstants.TopRankUpperBound.ToString(CultureInfo.InvariantCulture)} at "
            + $"{Scenario4SpecConstants.ReferenceShiftsTopRanks.ToString(CultureInfo.InvariantCulture)} shifts = "
            + $"{Scenario4SpecConstants.ReferenceHoursTopRanks.ToString("0.#", CultureInfo.InvariantCulture)} h, rank "
            + $"{Scenario4SpecConstants.RemainderRank.ToString(CultureInfo.InvariantCulture)} with the remaining "
            + $"{Scenario4SpecConstants.ReferenceShiftsRemainderRank.ToString(CultureInfo.InvariantCulture)} shifts, "
            + "the last two ranks with nothing. All ranks: "
            + $"{Scenario4Diagnostics.DescribeHours(hours.PerEmployee)}. {Describe(problems)}");
    }

    [Test]
    public void A7_RotationRunsForward()
    {
        var rotation = Metrics.Rotation;

        rotation.ForwardRate.ShouldBeGreaterThanOrEqualTo(
            AutofillSpecConstants.MinForwardRotationRate,
            "A7: at least "
            + $"{(AutofillSpecConstants.MinForwardRotationRate * 100).ToString("0.#", CultureInfo.InvariantCulture)} % "
            + "of the package transitions must follow early to late to night to early, but only "
            + $"{(rotation.ForwardRate * 100).ToString("0.#", CultureInfo.InvariantCulture)} % of "
            + $"{rotation.Transitions.Count.ToString(CultureInfo.InvariantCulture)} transition(s) do; "
            + $"{rotation.BackwardOrSkipCount.ToString(CultureInfo.InvariantCulture)} go backward or skip a kind.");
    }

    [Test]
    public void A8_S4_12_FairnessHoldsInBothDimensions()
    {
        var counts = Metrics.Fairness.ShiftTypeCountPerEmployee
            .Where(c => c.ListRank <= Scenario4SpecConstants.TopRankUpperBound)
            .ToList();
        var spread = AutofillPlanAnalyzer.SpreadOf(counts);
        var widest = Math.Max(spread.Early, Math.Max(spread.Late, spread.Night));
        var problems = new List<string>();

        if (widest > AutofillSpecConstants.MaxShiftKindSpread)
        {
            problems.Add(
                "the shift-kind spread over the cohort of ranks 1 to "
                + $"{Scenario4SpecConstants.TopRankUpperBound.ToString(CultureInfo.InvariantCulture)} is "
                + $"early={spread.Early.ToString(CultureInfo.InvariantCulture)}, "
                + $"late={spread.Late.ToString(CultureInfo.InvariantCulture)}, "
                + $"night={spread.Night.ToString(CultureInfo.InvariantCulture)}, above the limit of "
                + $"{AutofillSpecConstants.MaxShiftKindSpread.ToString(CultureInfo.InvariantCulture)}");
        }

        var meanRanks = Metrics.Orders.EmployeeDistribution.Select(d => d.MeanListRank).ToList();
        if (meanRanks.Count > 1)
        {
            var rankSpread = meanRanks.Max() - meanRanks.Min();
            if (rankSpread > Scenario4SpecConstants.MaxOrderMeanRankSpread)
            {
                problems.Add(
                    "the orders are staffed from different parts of the list: mean list rank spread "
                    + $"{rankSpread.ToString("0.###", CultureInfo.InvariantCulture)} above the limit of "
                    + $"{Scenario4SpecConstants.MaxOrderMeanRankSpread.ToString("0.#", CultureInfo.InvariantCulture)}");
            }
        }

        problems.ShouldBeEmpty(
            "A8/S4-12: fairness has two dimensions here. The shift kinds must be spread evenly over the cohort that "
            + "the top-down rule actually serves, and no order may be staffed systematically by higher or lower ranks "
            + "than another — three identically cut orders must not turn into a first-class and a third-class order. "
            + $"Counts: {Scenario4Diagnostics.DescribeShiftCounts(counts)}. Order distribution: "
            + $"{Scenario4Diagnostics.DescribeOrderDistribution(Metrics.Orders.EmployeeDistribution)}. "
            + Describe(problems));
    }

    [Test]
    public void A9_TwoRunsWithTheSameSeedProduceTheSamePlan()
    {
        Run.RunsIdentical.ShouldBeTrue(
            $"A9: two runs with seed {Definition.Config.RandomSeed.ToString(CultureInfo.InvariantCulture)}, sequential "
            + "evaluation and no wall-clock budget must produce the identical plan, but they differ at "
            + $"{Run.FirstDifference}");
    }

    [Test]
    public void S4_14_TheRepeatedRunMeasuresTheSame()
    {
        var differences = Scenario4SymmetryComparison.Compare(Run.Metrics, Run.SecondMetrics);

        differences.ShouldBeEmpty(
            "S4-14: the repeat of the run must not only produce the same plan but also the same measurement — a "
            + "difference here would mean the analyzer itself is not deterministic, which would invalidate every "
            + $"other assertion: {Scenario4Diagnostics.DescribeMetricDifferences(differences)}");
    }

    [Test]
    public void A10_S4_7_CarriedInPackagesKeepOrderKindAndLength()
    {
        SkipWithoutPreviousMonth();
        var openEmployees = Definition.OpenCarryIns.Select(c => c.AgentId).ToHashSet(StringComparer.Ordinal);
        var measured = Metrics.CarryInThreeDimensional.Where(c => openEmployees.Contains(c.Employee)).ToList();
        var wrong = measured.Where(c => !c.Ok).ToList();

        wrong.ShouldBeEmpty(
            "A10/S4-7: an employee still inside a previous-month package must continue it in all three dimensions — "
            + "the same order, the same shift kind and exactly the number of days that were still missing. "
            + $"{wrong.Count.ToString(CultureInfo.InvariantCulture)} of "
            + $"{measured.Count.ToString(CultureInfo.InvariantCulture)} do not: "
            + $"{Scenario4Diagnostics.DescribeCarryInThreeDimensional(wrong)}. Same measurement on the auction seed "
            + "plan, before any evolution ran: "
            + Scenario4Diagnostics.DescribeCarryInThreeDimensional(
                Run.SeedMetrics.CarryInThreeDimensional.Where(c => openEmployees.Contains(c.Employee)))
            + ". An identical failure there places the defect in the seeding, not in the evolution.");
    }

    [Test]
    public void A11_CarriedInPackagesAreCompletedToFiveShifts()
    {
        SkipWithoutPreviousMonth();
        var openEmployees = Definition.OpenCarryIns.Select(c => c.AgentId).ToHashSet(StringComparer.Ordinal);
        var wrong = Metrics.CarryInThreeDimensional
            .Where(c => openEmployees.Contains(c.Employee))
            .Where(c => c.ActualRemainingDays != c.ExpectedRemainingDays)
            .ToList();

        wrong.ShouldBeEmpty(
            "A11: a started package must be completed to exactly "
            + $"{AutofillSpecConstants.MaxWorkDays.ToString(CultureInfo.InvariantCulture)} shifts on its own order and "
            + "kind instead of being started anew: "
            + Scenario4Diagnostics.DescribeCarryInThreeDimensional(wrong));
    }

    [Test]
    public void A12_TwoFreeDaysFollowTheCompletedCarryInPackage()
    {
        SkipWithoutPreviousMonth();
        var problems = new List<string>();

        foreach (var carryIn in Definition.OpenCarryIns)
        {
            var packages = Metrics.Packages.Items
                .Where(p => string.Equals(p.Employee, carryIn.AgentId, StringComparison.Ordinal))
                .OrderBy(p => p.StartDate)
                .ToList();
            var continued = packages.FirstOrDefault(
                p => p.StartDate <= Definition.PeriodFrom && p.EndDate >= Definition.PeriodFrom);
            if (continued is null)
            {
                problems.Add(
                    $"{carryIn.AgentId} holds no shift on {Definition.PeriodFrom:yyyy-MM-dd}, so the carried-in "
                    + "package was not continued at all");
                continue;
            }

            var next = packages.FirstOrDefault(p => p.StartDate > continued.EndDate);
            var freeDays = next is null
                ? Definition.PeriodUntil.DayNumber - continued.EndDate.DayNumber
                : next.StartDate.DayNumber - continued.EndDate.DayNumber - 1;
            if (freeDays < AutofillSpecConstants.MinFreeDaysAfterCarryIn)
            {
                problems.Add(
                    $"{carryIn.AgentId}: the continued package ends on {continued.EndDate:yyyy-MM-dd} and is followed "
                    + $"by only {freeDays.ToString(CultureInfo.InvariantCulture)} free day(s)");
            }
        }

        problems.ShouldBeEmpty(
            $"A12: at least {AutofillSpecConstants.MinFreeDaysAfterCarryIn.ToString(CultureInfo.InvariantCulture)} "
            + "consecutive free days must follow the completion of a carried-in package. Measured on the whole package "
            + "that covers the first day of the period, February part included. " + Describe(problems));
    }

    [Test]
    public void A13_S4_8_ClosedPackagesRotateAcrossTheMonthBoundary()
    {
        SkipWithoutPreviousMonth();
        var openEmployees = Definition.OpenCarryIns.Select(c => c.AgentId).ToHashSet(StringComparer.Ordinal);
        var closed = Definition.CarryIns.Where(c => !openEmployees.Contains(c.AgentId)).ToList();
        var problems = new List<string>();

        foreach (var carryIn in closed)
        {
            var measured = Metrics.CarryInThreeDimensional
                .FirstOrDefault(c => string.Equals(c.Employee, carryIn.AgentId, StringComparison.Ordinal));
            if (measured is null)
            {
                problems.Add($"{carryIn.AgentId} was not measured at all, so fixture and metrics disagree");
                continue;
            }

            if (measured.ActualShiftType == carryIn.ExpectedFirstShiftKind)
            {
                continue;
            }

            problems.Add(
                $"{carryIn.AgentId} closed a {carryIn.Kind} package on {carryIn.PackageEndInclusive:yyyy-MM-dd}, so its "
                + $"first package of the period must rotate to {carryIn.ExpectedFirstShiftKind}, but it is "
                + (measured.ActualShiftType?.ToString() ?? "absent — the employee received no in-period shift"));
        }

        problems.ShouldBeEmpty(
            "A13/S4-8: a package that ended in the previous month must rotate forward, and the rotation belongs to the "
            + "EMPLOYEE, not to the order — the specification's table names the next shift kind and deliberately names "
            + "no order, so the order the employee lands on is measured and not judged. Measured: "
            + $"{Scenario4Diagnostics.DescribeCarryInThreeDimensional(Metrics.CarryInThreeDimensional.Where(c => !openEmployees.Contains(c.Employee)))}. "
            + Describe(problems));
    }

    [Test]
    public void A14_S4_9_NothingBeforeThePeriodIsChanged()
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
            "A14/S4-9: no shift dated before the period may be changed. Measured as the fixed February works being "
            + "byte-identical before and after the run — agent, day, span, hours, shift reference and work id — plus no "
            + $"assignment being planned outside the period. {Describe(problems)}");
    }

    [Test]
    public void S4_11_ExtendedFreeDaysOfTheLowestRanksAreForced()
    {
        SkipWithoutPreviousMonth();
        var forced = Metrics.Packages.ForcedExtraFreeDays
            .Select(f => f.Employee)
            .ToHashSet(StringComparer.Ordinal);
        var missing = Scenario4SpecConstants.ForcedIdleOnFirstDay.Where(e => !forced.Contains(e)).ToList();

        missing.ShouldBeEmpty(
            "S4-11: the two lowest ranks are available from the first day of the period — their previous-month package "
            + "ended on 26 February and the two contractual rest days are over — but all nine slots of that day are "
            + "held by the nine running packages. Their extended free days are therefore FORCED, not chosen, and the "
            + "measurement must say so. The plan itself records no such flag, so the measure derives it from the slots "
            + "that stayed open. Read this one together with A10/S4-7: the hard proof that the two ranks are forced is "
            + "the fixture guard, which computes it from the previous-month end dates without any plan. This test is "
            + "the secondary check that the finished plan agrees, and it can only disagree if the nine running "
            + "packages were NOT continued and the two ranks slipped into the first day — which is the A10 defect "
            + "showing up a second time, not an independent one. Forced runs found: "
            + $"{Scenario4Diagnostics.DescribeForcedFreeDays(Metrics.Packages.ForcedExtraFreeDays)}. Missing: "
            + string.Join(", ", missing));
    }

    [Test]
    public void S4_5_OrderLoyaltyInsideThePackageIsMeasured()
    {
        var switches = Metrics.Orders.SwitchesWithinPackage;
        var pure = Metrics.Packages.Items.Count - switches.Count;

        TestContext.Out.WriteLine(
            $"S4-5 measured, not asserted: {switches.Count.ToString(CultureInfo.InvariantCulture)} of "
            + $"{Metrics.Packages.Items.Count.ToString(CultureInfo.InvariantCulture)} packages touch more than one "
            + $"order, {pure.ToString(CultureInfo.InvariantCulture)} stay on one. "
            + Scenario4Diagnostics.DescribeOrderSwitchesInPackage(switches));

        Assert.Ignore(
            "S4-5 is a measurement, not a verdict: phase A' proved that no engine mechanism keeps an employee on one "
            + "order. The stage-3 continuity term reads a location field every producing site sets to null, and the "
            + "top-down handover moves assignments by rank, hours and shift kind alone. The number above is the input "
            + "for the owner decision whether order loyalty is to become a rule.");
    }

    [Test]
    public void S4_6_OrderLoyaltyAcrossPackagesIsMeasured()
    {
        var perEmployee = Metrics.Orders.SwitchesPerEmployee;
        var median = Scenario4Diagnostics.MedianOf(perEmployee.Select(s => s.SwitchCount).ToList());

        TestContext.Out.WriteLine(
            $"S4-6 measured, not asserted: median order changes per employee "
            + $"{median.ToString("0.#", CultureInfo.InvariantCulture)}, reference "
            + $"{Scenario4SpecConstants.OrderSwitchMedianReference.ToString(CultureInfo.InvariantCulture)}. "
            + Scenario4Diagnostics.DescribeOrderSwitchesPerEmployee(perEmployee));

        Assert.Ignore(
            "S4-6 is a measurement for the same reason as S4-5. The reference median of "
            + $"{Scenario4SpecConstants.OrderSwitchMedianReference.ToString(CultureInfo.InvariantCulture)} is written "
            + "into the report next to the measured one so the gap has a number.");
    }

    [Test]
    public void S4_16_TheRunFinishedAndItsRuntimeIsReported()
    {
        var runtime = Metrics.Runtime;
        var seconds = runtime.WallClockMs / (double)MillisecondsPerSecond;

        TestContext.Out.WriteLine(
            $"S4-16 runtime of one engine run: {runtime.WallClockMs.ToString(CultureInfo.InvariantCulture)} ms "
            + $"({seconds.ToString("0.#", CultureInfo.InvariantCulture)} s), population "
            + $"{Definition.Config.PopulationSize.ToString(CultureInfo.InvariantCulture)}, generations "
            + $"{Definition.Config.MaxGenerations.ToString(CultureInfo.InvariantCulture)}.");

        if (seconds > Scenario4SpecConstants.RuntimeFindingSeconds)
        {
            TestContext.Out.WriteLine(
                $"S4-16 finding, informative: the run took longer than "
                + $"{Scenario4SpecConstants.RuntimeFindingSeconds.ToString(CultureInfo.InvariantCulture)} s.");
        }

        runtime.WallClockMs.ShouldBeGreaterThan(
            0,
            "S4-16: the run must have been timed; a wall clock of zero means the runner never measured it.");
    }

    /// <summary>
    /// Ends the current test as not evaluable when the scenario has no previous month. Without it the
    /// carry-in assertions would pass on an empty list and the result table would report a green
    /// continuation check for a run that has nothing to continue.
    /// </summary>
    protected void SkipWithoutPreviousMonth()
    {
        if (Definition.CarryIns.Count == 0)
        {
            Assert.Ignore(
                "not evaluable: this run has no previous month, so there is no carried-in package to continue, to "
                + "rotate away from or to be kept out of the first day by. An empty measurement is not a green one.");
        }
    }

    /// <summary>One line out of the collected problem texts; empty when there is nothing to report.</summary>
    /// <param name="problems">Problem lines of one assertion</param>
    protected static string Describe(IReadOnlyList<string> problems)
        => problems.Count == 0 ? string.Empty : "Problems: " + string.Join(ProblemSeparator, problems);

    private const int MillisecondsPerSecond = 1000;
}
