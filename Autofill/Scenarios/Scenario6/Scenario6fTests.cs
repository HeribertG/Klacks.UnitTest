// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario5;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario6;

/// <summary>
/// Run S6f — both preference employees on holiday at the same time, 9 to 22 March.
/// <para>
/// Fifteen employees remain for twelve assignments a day inside the window, an 80 % work share, and
/// every one of the 42 day shifts in it necessarily goes to somebody who never asked for one. The run
/// asks whether full coverage survives that: the preference is soft and may be outvoted at any time,
/// but coverage is rule 1 and may not be.
/// </para>
/// </summary>
[TestFixture]
[Explicit("Scenario 6 run; minutes of engine time. Select it by name.")]
[Category("Autofill")]
[Category("Scenario6")]
public class Scenario6fTests : Scenario6RunTestBase
{
    [OneTimeSetUp]
    public void BuildAndRunTheBothPreferenceScenario()
        => BuildGuardAndRun(
            Scenario6Fixture.BuildBothPreferenceRun,
            Scenario6SpecConstants.RunFArtifactName,
            ValidateBothAreAbsent);

    /// <summary>
    /// S6-14. Every demanded shift is staffed, both preference employees absent or not. The soft
    /// preference may be outvoted; the coverage rule may not.
    /// </summary>
    [Test]
    public void S6_14_EveryDemandedShiftIsStaffedWithoutBothPreferenceEmployees()
    {
        var coverage = Metrics.Coverage;

        coverage.FilledShifts.ShouldBe(
            Scenario5SpecConstants.TotalRequiredShifts,
            "S6-14: with both day-shift employees on holiday, all 42 day shifts inside the window go to employees "
            + "who never asked for one — which is arithmetic, not a violation, because a Preferred entry is a soft "
            + "term that only tilts the bidding. Coverage is a different matter: it is rule 1 and unstaffed slots "
            + $"are a failure whoever is absent. Unfilled: {coverage.UnfilledShifts.Count.ToString(CultureInfo.InvariantCulture)}, "
            + "first of them: "
            + string.Join(
                ", ",
                coverage.UnfilledShifts.Take(5).Select(u => $"{u.Date:MM-dd} {u.ShiftType}")));

        var short_ = coverage.AssignmentsPerDay
            .Where(d => d.Count != Definition.SlotsPerDay)
            .ToList();
        short_.ShouldBeEmpty(
            "S6-14: every single day of the period demands "
            + $"{Definition.SlotsPerDay.ToString(CultureInfo.InvariantCulture)} assignments over all orders, and the "
            + "days inside the absence window demand exactly as many as the days outside it. Days off the mark: "
            + string.Join(
                ", ",
                short_.Take(10).Select(d =>
                    $"{d.Date:MM-dd} {d.Count.ToString(CultureInfo.InvariantCulture)}")));
    }

    /// <summary>
    /// Checks the premise: both preference employees really are absent over the same window. With only
    /// one of them away the run would be S6a and S6-14 would test a weaker situation than it claims.
    /// </summary>
    /// <param name="definition">Assembled scenario to check</param>
    private static IReadOnlyList<string> ValidateBothAreAbsent(AutofillScenarioDefinition definition)
    {
        var problems = new List<string>();
        foreach (var employee in Scenario5SpecConstants.DayShiftEmployees)
        {
            problems.AddRange(Scenario6FixtureGuard.ValidateTwoWeekWindow(definition, employee));
        }

        var available = Scenario5SpecConstants.DayShiftEmployees
            .Count(e => definition.Absences.DaysOf(e).Count == 0);
        if (available != 0)
        {
            problems.Add(
                $"{available.ToString(CultureInfo.InvariantCulture)} of the two day-shift employees are still "
                + "available; this run is defined by both of them being away at once");
        }

        return problems;
    }
}
