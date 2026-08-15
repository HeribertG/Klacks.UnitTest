// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Fixtures;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario6;

/// <summary>
/// Run S6b — the calibration run: S6a plus an eighteenth employee at 165 guaranteed hours, without
/// keywords and without preferences.
/// <para>
/// It exists to separate two causes that S6a cannot tell apart. In S6a the roster offers 513 places
/// for 372 assignments, a work share of 72.5 % against the 71.43 % a clean five-work/two-free rhythm
/// carries — so the rhythm is arithmetically out of reach and a broken rhythm says nothing about the
/// algorithm. The eighteenth employee raises the roster to 544 places, 68.4 %, and the rhythm becomes
/// reachable again. Every rhythm assertion below therefore means something HERE that it cannot mean in
/// S6a: if the rhythm still breaks with room to spare, the cause is the algorithm and not the
/// arithmetic.
/// </para>
/// </summary>
[TestFixture]
[Explicit("Scenario 6 run; minutes of engine time. Select it by name.")]
[Category("Autofill")]
[Category("Scenario6")]
public class Scenario6bTests : Scenario6RunTestBase
{
    [OneTimeSetUp]
    public void BuildAndRunTheCalibrationScenario()
        => BuildGuardAndRun(
            Scenario6Fixture.BuildCalibrationRun,
            Scenario6SpecConstants.RunBArtifactName,
            definition => ValidateCalibrationSetup(definition));

    /// <summary>
    /// S6-16. With the rhythm arithmetically reachable, packages of the contractual five days must
    /// dominate: at least seventy per cent of them.
    /// </summary>
    [Test]
    public void S6_16_MostPackagesReachTheContractualFiveDays()
    {
        var packages = Metrics.Packages.Items;
        packages.Count.ShouldBeGreaterThan(0, "S6-16 needs packages to judge.");

        var fiveDay = packages.Count(p => p.LengthDays == AutofillSpecConstants.MaxWorkDays);
        var share = fiveDay / (double)packages.Count;

        share.ShouldBeGreaterThanOrEqualTo(
            Scenario6SpecConstants.CalibrationFiveDayPackageShare,
            "S6-16: this run has capacity to spare — 68.4 % work share against the 71.43 % a clean 5/2 rhythm "
            + "carries — so the five-day package is available and must dominate. In S6a the same share would be "
            + $"arithmetically impossible and would say nothing. Measured {Share(share)} over "
            + $"{Count(packages.Count)} packages, {Count(fiveDay)} of them five days long; the length histogram is "
            + string.Join(", ", Metrics.Packages.LengthHistogram.Select(e => $"{e.Key}:{Count(e.Value)}")));
    }

    /// <summary>S6-17. The free-block histogram must peak at the two contractual rest days.</summary>
    [Test]
    public void S6_17_TheFreeBlockHistogramPeaksAtTwo()
    {
        var histogram = Metrics.Packages.FreeBlockHistogram;
        var mode = histogram.Count == 0
            ? 0
            : histogram.OrderByDescending(e => e.Value).ThenBy(e => e.Key).First().Key;

        mode.ShouldBe(
            Scenario6SpecConstants.CalibrationFreeBlockMode,
            "S6-17: with the rhythm reachable, the ordinary free block is the pair of contractual rest days. Absence "
            + "days are NOT part of this histogram — they are blocked and paid, not free — so a fourteen-day holiday "
            + "cannot create a fourteen-day free block and shift the mode. Measured: "
            + string.Join(", ", histogram.Select(e => $"{Count(e.Key)}d x{Count(e.Value)}")));
    }

    /// <summary>
    /// S6-18. Every rank except the absent one must reach at least 95 % of its guaranteed hours. The
    /// absent employee is excluded by name: his hours come partly from the absence credit, which the
    /// planned-hours metric does not contain, so judging him by planned hours alone would measure the
    /// holiday rather than the plan. His target is S6-6's business.
    /// </summary>
    [Test]
    public void S6_18_EveryRankExceptTheAbsentOneReachesItsGuarantee()
    {
        var absent = Definition.Absences.Employees().ToHashSet(StringComparer.Ordinal);
        var short_ = Metrics.Hours.PerEmployee
            .Where(h => !absent.Contains(h.Employee))
            .Where(h => h.FulfillmentPct < Scenario6SpecConstants.CalibrationFulfilmentThreshold)
            .ToList();

        short_.ShouldBeEmpty(
            "S6-18: this run has the capacity for everybody, so every rank that is not on holiday must reach "
            + $"{Share(Scenario6SpecConstants.CalibrationFulfilmentThreshold)} of its guarantee. The absent employee "
            + "is judged by S6-6 instead, because his hours arrive partly as an absence credit that planned hours do "
            + "not contain. Below the threshold: "
            + string.Join(
                ", ",
                short_.Select(h => $"{h.Employee} {Share(h.FulfillmentPct)} of {Hours(h.GuaranteedHours)} h")));
    }

    /// <summary>S6-19. The shift kinds must be spread evenly: at most two apart between the extremes.</summary>
    [Test]
    public void S6_19_TheShiftKindsAreSpreadEvenly()
    {
        var spread = Metrics.Fairness.SpreadPerType;
        var problems = new List<string>();

        if (spread.Early > Scenario6SpecConstants.CalibrationMaxShiftKindSpread
            || spread.Late > Scenario6SpecConstants.CalibrationMaxShiftKindSpread)
        {
            problems.Add(
                $"the early/late spread is {Count(spread.Early)}/{Count(spread.Late)}");
        }

        var nightCohort = Metrics.Fairness.ShiftTypeCountPerEmployee
            .Where(c => !Scenario5.Scenario5SpecConstants.DayShiftEmployees
                .Contains(c.Employee, StringComparer.Ordinal))
            .ToList();
        var nightSpread = nightCohort.Count == 0 ? 0 : nightCohort.Max(c => c.Night) - nightCohort.Min(c => c.Night);
        if (nightSpread > Scenario6SpecConstants.CalibrationMaxShiftKindSpread)
        {
            problems.Add(
                $"inside the {Count(nightCohort.Count)}-employee night cohort the spread is {Count(nightSpread)}");
        }

        problems.ShouldBeEmpty(
            "S6-19: the night spread is measured over the cohort that may take a night shift at all — including the "
            + "two blacklisted employees would compare people forbidden the shift with people who are not and would "
            + $"read as unfairness where the fixture is the cause. Tolerated spread "
            + $"{Count(Scenario6SpecConstants.CalibrationMaxShiftKindSpread)}. " + Describe(problems));
    }

    /// <summary>
    /// Checks what makes this run a calibration run rather than a second main run: the eighteenth
    /// employee is there, he carries the lower guarantee, and the capacity he adds actually brings the
    /// work share below the 5/2 ceiling. Without the last of those the four assertions above would ask
    /// for a rhythm that is still out of reach and would be red for an arithmetic reason.
    /// </summary>
    /// <param name="definition">Assembled scenario to check</param>
    private static IReadOnlyList<string> ValidateCalibrationSetup(AutofillScenarioDefinition definition)
    {
        var problems = new List<string>();

        if (definition.EmployeesInListOrder.Count != Scenario6SpecConstants.CalibrationEmployeeCount)
        {
            problems.Add(
                $"the roster holds {Count(definition.EmployeesInListOrder.Count)} employees instead of "
                + Count(Scenario6SpecConstants.CalibrationEmployeeCount));
        }

        var eighteenth = definition.Context.Agents
            .FirstOrDefault(a => a.Id == Scenario6SpecConstants.EighteenthEmployee);
        if (eighteenth is null)
        {
            problems.Add($"'{Scenario6SpecConstants.EighteenthEmployee}' is not part of the scenario");
        }
        else if (Math.Abs(eighteenth.GuaranteedHours - Scenario6SpecConstants.EighteenthEmployeeGuaranteedHours)
                 > double.Epsilon)
        {
            problems.Add(
                $"'{Scenario6SpecConstants.EighteenthEmployee}' carries "
                + $"{Hours(eighteenth.GuaranteedHours)} h instead of "
                + Hours(Scenario6SpecConstants.EighteenthEmployeeGuaranteedHours));
        }

        var availability = Analysis.AbsenceAnalyzer.BuildAvailability(definition);
        if (availability.WorkRatioRequired >= availability.MaxRatioUnder52)
        {
            problems.Add(
                $"the work share is {Share(availability.WorkRatioRequired)}, still at or above the 5/2 ceiling of "
                + $"{Share(availability.MaxRatioUnder52)} — the eighteenth employee did not make the rhythm "
                + "reachable, so the rhythm assertions of this run would be red for an arithmetic reason");
        }

        problems.AddRange(Scenario6FixtureGuard.ValidateTwoWeekWindow(
            definition, Scenario6SpecConstants.HolidayEmployee));
        return problems;
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Hours(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Share(double value)
        => (value * 100).ToString("0.0", CultureInfo.InvariantCulture) + " %";
}
