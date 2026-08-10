// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Analysis;
using Klacks.UnitTest.Autofill.Fixtures;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario4;

/// <summary>
/// Run S4b — the calibration variant: the identical fixture at 150 guaranteed hours, so supply
/// (15 x 150 h = 2250 h) and demand (2232 h) almost match and every goal is satisfiable at the same
/// time. Where S4a has to choose which rule to break, S4b has no excuse: a red assertion here is an
/// algorithmic defect and not an unavoidable goal conflict, which is exactly what the calibration is
/// for.
/// </summary>
[TestFixture]
[Explicit("Scenario 4 run; minutes of engine time. Select it by name.")]
[Category("Autofill")]
[Category("Scenario4")]
public class Scenario4bTests : Scenario4RunTestBase
{
    [OneTimeSetUp]
    public void BuildAndRunTheCalibration()
        => BuildGuardAndRun(Scenario4CarryInFixture.BuildCalibrationRun, Scenario4SpecConstants.RunBArtifactName);

    [Test]
    public void S4_15_AllGoalsAreReachedAtOnce()
    {
        var problems = new List<string>();

        var fullyMet = Metrics.Hours.PerEmployee
            .Where(e => e.PlannedHours + HoursComparisonEpsilon >= AutofillSpecConstants.CalibrationGuaranteedHours)
            .ToList();
        if (fullyMet.Count < Scenario4SpecConstants.MinimumFullyMetGuarantees)
        {
            problems.Add(
                $"B6: only {fullyMet.Count.ToString(CultureInfo.InvariantCulture)} of "
                + $"{Scenario4SpecConstants.EmployeeCount.ToString(CultureInfo.InvariantCulture)} ranks fully reach "
                + $"the {AutofillSpecConstants.CalibrationGuaranteedHours.ToString("0.#", CultureInfo.InvariantCulture)} h "
                + "guarantee, below the measured optimum of "
                + $"{Scenario4SpecConstants.MinimumFullyMetGuarantees.ToString(CultureInfo.InvariantCulture)} that "
                + "stage 1 of the fitness holds (Stage1GuaranteeDominanceTests)");
        }

        var remainderFloor =
            AutofillSpecConstants.CalibrationGuaranteedHours * Scenario4SpecConstants.RemainingRankFloorShare;
        var starved = Metrics.Hours.PerEmployee
            .Where(e => e.PlannedHours + HoursComparisonEpsilon < remainderFloor)
            .ToList();
        if (starved.Count > 0)
        {
            problems.Add(
                "B6: every rank below the full guarantee must still reach "
                + $"{remainderFloor.ToString("0.#", CultureInfo.InvariantCulture)} h "
                + $"({(Scenario4SpecConstants.RemainingRankFloorShare * 100).ToString("0.#", CultureInfo.InvariantCulture)} % "
                + "of the guarantee), but " + Scenario4Diagnostics.DescribeHours(starved) + " stay below");
        }

        var histogram = Metrics.Packages.FreeBlockHistogram;
        if (histogram.Count == 0)
        {
            problems.Add("the plan contains no free block at all");
        }
        else
        {
            var mode = histogram.OrderByDescending(e => e.Value).ThenBy(e => e.Key).First().Key;
            if (mode < Scenario4SpecConstants.CalibrationFreeBlockLow
                || mode > Scenario4SpecConstants.CalibrationFreeBlockHigh)
            {
                problems.Add(
                    $"the free-block histogram peaks at {mode.ToString(CultureInfo.InvariantCulture)} days instead of "
                    + $"{Scenario4SpecConstants.CalibrationFreeBlockLow.ToString(CultureInfo.InvariantCulture)} to "
                    + $"{Scenario4SpecConstants.CalibrationFreeBlockHigh.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        if (Metrics.Rotation.ForwardRate < Scenario4SpecConstants.CalibrationMinForwardRotationRate)
        {
            problems.Add(
                "the forward rotation rate is "
                + $"{(Metrics.Rotation.ForwardRate * 100).ToString("0.#", CultureInfo.InvariantCulture)} %, below the "
                + $"{(Scenario4SpecConstants.CalibrationMinForwardRotationRate * 100).ToString("0.#", CultureInfo.InvariantCulture)} % "
                + "the calibration requires");
        }

        var spread = AutofillPlanAnalyzer.SpreadOf(Metrics.Fairness.ShiftTypeCountPerEmployee);
        var widest = Math.Max(spread.Early, Math.Max(spread.Late, spread.Night));
        if (widest > AutofillSpecConstants.MaxShiftKindSpread)
        {
            problems.Add(
                $"the shift-kind spread over all fifteen employees is {widest.ToString(CultureInfo.InvariantCulture)}, "
                + $"above {AutofillSpecConstants.MaxShiftKindSpread.ToString(CultureInfo.InvariantCulture)}");
        }

        problems.ShouldBeEmpty(
            "S4-15 (owner decision B6): at 150 guaranteed hours supply and demand almost match, so most goals are "
            + "reachable at the same time — at least "
            + $"{Scenario4SpecConstants.MinimumFullyMetGuarantees.ToString(CultureInfo.InvariantCulture)} of "
            + $"{Scenario4SpecConstants.EmployeeCount.ToString(CultureInfo.InvariantCulture)} ranks fully reach the "
            + "guarantee (stage 1's measured optimum; a uniform 95 % floor for all fifteen is arithmetically "
            + "unreachable and was dropped), every remaining rank at least "
            + $"{(Scenario4SpecConstants.RemainingRankFloorShare * 100).ToString("0.#", CultureInfo.InvariantCulture)} % "
            + "of its target, free blocks peaking at two to three days, forward rotation at least 85 % and a "
            + "shift-kind spread of at most two over the WHOLE roster, not just the top ranks. Free blocks: "
            + $"{Scenario4Diagnostics.DescribeFreeBlocks(histogram)}. Hours: "
            + $"{Scenario4Diagnostics.DescribeHours(Metrics.Hours.PerEmployee)}. {Describe(problems)}");
    }

    private const double HoursComparisonEpsilon = 1e-9;
}
