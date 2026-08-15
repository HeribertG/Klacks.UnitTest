// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario6;

/// <summary>
/// Run S6c — the holiday on list rank 1. Two independent questions at once, and neither can be read
/// off the other.
/// <para>
/// The first is the top-down order: rank 1 is served first, so an absence there has to be visible in
/// the hour picture instead of being smoothed away. The second is redundancy: MA-01's FREE window of
/// 10 to 12 March lies entirely inside the holiday, so two hard blocks with two separate vetoes and no
/// shared predicate point at the same three days. They must produce one blocked day and never a
/// conflict — a plan that treats the overlap as a contradiction would be broken by an input that says
/// the same thing twice.
/// </para>
/// </summary>
[TestFixture]
[Explicit("Scenario 6 run; minutes of engine time. Select it by name.")]
[Category("Autofill")]
[Category("Scenario6")]
public class Scenario6cTests : Scenario6RunTestBase
{
    [OneTimeSetUp]
    public void BuildAndRunTheTopRankScenario()
        => BuildGuardAndRun(
            Scenario6Fixture.BuildTopRankRun,
            Scenario6SpecConstants.RunCArtifactName,
            definition => Scenario6FixtureGuard.ValidateTwoWeekWindow(
                definition, Scenario6SpecConstants.TopRankHolidayEmployee));

    /// <summary>
    /// S6-10. The FREE window inside the holiday is redundant and must stay conflict-free: the three
    /// days carry both inputs, both of them block, and the result is one blocked day with no keyword
    /// violation and no assignment.
    /// </summary>
    [Test]
    public void S6_10_TheFreeWindowInsideTheHolidayIsRedundantAndConflictFree()
    {
        var problems = new List<string>();
        var redundant = Absences.RedundantWithKeyword
            .Where(r => r.Keyword == ScheduleCommandKeyword.Free)
            .ToList();

        TestContext.Out.WriteLine(
            $"S6-10: {redundant.Count.ToString(CultureInfo.InvariantCulture)} day(s) carry both a holiday row and a "
            + "FREE command: "
            + string.Join(", ", redundant.Select(r => $"{r.Employee} {r.Date:MM-dd}")));

        var expectedDays = Scenario6SpecConstants.FreeWindowUntil.DayNumber
            - Scenario6SpecConstants.FreeWindowFrom.DayNumber + 1;
        if (redundant.Count != expectedDays)
        {
            problems.Add(
                $"{redundant.Count.ToString(CultureInfo.InvariantCulture)} day(s) carry both inputs instead of "
                + $"{expectedDays.ToString(CultureInfo.InvariantCulture)} — the FREE window "
                + $"{Scenario6SpecConstants.FreeWindowFrom:MM-dd}..{Scenario6SpecConstants.FreeWindowUntil:MM-dd} "
                + "does not lie inside the holiday, so this run tests nothing about redundancy");
        }

        if (redundant.Any(r => !r.BothBlockEveryShift))
        {
            problems.Add("a day carries both inputs but they do not forbid the same thing");
        }

        var keywordViolations = Metrics.Keyword.ScheduleCommandViolations
            .Where(v => string.Equals(
                v.Employee, Scenario6SpecConstants.TopRankHolidayEmployee, StringComparison.Ordinal))
            .ToList();
        if (keywordViolations.Count > 0)
        {
            problems.Add(
                $"{keywordViolations.Count.ToString(CultureInfo.InvariantCulture)} keyword violation(s) on the "
                + "employee whose FREE window the holiday already covers");
        }

        problems.ShouldBeEmpty(
            "S6-10: an absence and a FREE command are two independent hard blocks — separate engine fields, separate "
            + "vetoes (Stage0HardConstraintChecker BreakBlocker at :164 and KeywordFree at :170), no shared "
            + "availability predicate. Stated on the same day they are redundant, and redundancy must be harmless: "
            + "one blocked day, no violation of either. The only asymmetry is the hours — the absence pays, the FREE "
            + "command does not — and that difference belongs to S6-6, not here. " + Describe(problems));
    }

    /// <summary>
    /// The absence on rank 1 has to be visible in the hour picture rather than smoothed away. It is a
    /// MEASUREMENT and not a rule: the top-down order says rank 1 is served first, and the specification
    /// does not say what serving a rank first means when that rank is absent for two of four weeks.
    /// </summary>
    [Test]
    public void S6c_ReportsWhatAnAbsenceOnRankOneDoesToTheTopDownPicture()
    {
        var perEmployee = Metrics.Hours.PerEmployee;
        perEmployee.Count.ShouldBeGreaterThan(0, "the hour measurement is empty");

        var rankOne = perEmployee.First();
        TestContext.Out.WriteLine(
            $"S6c: rank 1 ({rankOne.Employee}) is on holiday and holds "
            + $"{rankOne.PlannedHours.ToString("0.#", CultureInfo.InvariantCulture)} planned hours against a "
            + $"guarantee of {rankOne.GuaranteedHours.ToString("0.#", CultureInfo.InvariantCulture)} h; "
            + $"the credit adds {((double)Definition.Absences.CreditHoursOf(rankOne.Employee)).ToString("0.##", CultureInfo.InvariantCulture)} h. "
            + $"Monotonicity violations in this run: {Metrics.Hours.MonotonicityViolations.Count.ToString(CultureInfo.InvariantCulture)}");

        rankOne.Employee.ShouldBe(
            Scenario6SpecConstants.TopRankHolidayEmployee,
            "S6c places the holiday on list rank 1; if the roster order moved, the run measures a different question.");
    }
}
