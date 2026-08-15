// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Fixtures;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario6;

/// <summary>
/// Run S6d — the holiday across the month boundary, 23 February to 8 March, on MA-04.
/// <para>
/// WHAT THE ENGINE CAN AND CANNOT SEE, and why the fixture models only half the window. An absence is
/// not a range: it is a set of one-day rows, and the Api's blocker query filters them to the planning
/// period. The February half therefore never reaches the engine — not because this fixture leaves it
/// out, but because production would not deliver it either. Modelling it anyway would test a code path
/// that does not exist. It is documented here and in the guard note, and the run measures the half
/// that IS visible: the seam.
/// </para>
/// <para>
/// The seam is the interesting part. MA-04 carries an open previous-month package that ends on 28
/// February and still owes four days into March; the holiday takes every one of them. Hard input
/// against hard input, and the absence wins — the carry-in is a continuation the plan is expected to
/// make, while the absence is a block it may not cross. The cut has to be REPORTED as forced with a
/// cause, not silently swallowed as a short package.
/// </para>
/// <para>
/// FOR THE REPORT, so phase C does not count it twice: the inherited assertion S5-5 — eleven
/// carried-in packages continued — is EXPECTED to be red in this run and only in this run. One of the
/// eleven is MA-04's, and the holiday makes continuing it illegal. That red is the fixture working as
/// designed and is the same fact S6-11 states positively; it is not a scenario-6 defect.
/// </para>
/// </summary>
[TestFixture]
[Explicit("Scenario 6 run; minutes of engine time. Select it by name.")]
[Category("Autofill")]
[Category("Scenario6")]
public class Scenario6dTests : Scenario6RunTestBase
{
    [OneTimeSetUp]
    public void BuildAndRunTheCrossMonthScenario()
        => BuildGuardAndRun(
            Scenario6Fixture.BuildCrossMonthRun,
            Scenario6SpecConstants.RunDArtifactName,
            ValidateCrossMonthSetup);

    /// <summary>
    /// S6-11. February is untouched and the carry-in cut is declared. Two halves of one statement: the
    /// plan may not reach back over the period start, and where the absence took the carried-in
    /// package's remaining days, the cut must stand in the artifact as forced with cause=absence.
    /// </summary>
    [Test]
    public void S6_11_FebruaryStaysUntouchedAndTheCarryInCutIsDeclared()
    {
        var problems = new List<string>();

        Run.CarryInUnchanged.ShouldBeTrue(
            "S6-11: the fixed previous month is input, never output. The runner fingerprints it before and after the "
            + $"run: {Run.CarryInDifference}");

        var beforePeriod = Run.Plan.Tokens.Where(t => t.Date < Definition.PeriodFrom).ToList();
        if (beforePeriod.Count > 0)
        {
            problems.Add(
                $"{beforePeriod.Count.ToString(CultureInfo.InvariantCulture)} assignment(s) carry a date before the "
                + "period start, so the plan reached back into February");
        }

        var cuts = Metrics.Packages.AbsenceCuts
            .Where(c => string.Equals(
                c.Employee, Scenario6SpecConstants.CrossMonthHolidayEmployee, StringComparison.Ordinal))
            .ToList();

        TestContext.Out.WriteLine("S6-11 absence cuts: " + Scenario6Diagnostics.DescribeCuts(cuts));

        if (cuts.Count == 0)
        {
            problems.Add(
                $"'{Scenario6SpecConstants.CrossMonthHolidayEmployee}' carries an open previous-month package whose "
                + "remaining days all fall inside the holiday, but no absence cut was declared for him");
        }

        if (cuts.Any(c => !c.Forced
                          || !string.Equals(c.Cause, Analysis.AbsenceAnalyzer.AbsenceCause, StringComparison.Ordinal)))
        {
            problems.Add("an absence cut was declared without being forced or without the absence cause");
        }

        problems.ShouldBeEmpty(
            "S6-11: the February half of this holiday is structurally invisible to the engine — an absence is a set "
            + "of day rows and the blocker query filters them to the period — so the fixture models the in-period "
            + "days only and the run measures the SEAM instead. There the carried-in package still owes four days "
            + "and the absence takes them: hard input against hard input, and the absence wins. That shortening must "
            + "be declared as forced with cause=absence rather than counted as an ordinary short package, or the "
            + "report cannot tell an absence cut from an algorithmic one. " + Describe(problems));
    }

    /// <summary>
    /// Checks what makes this the cross-month run: the modelled window starts on the first day of the
    /// period, the employee really does carry an open previous-month package, and every day that
    /// package still owes is an absence day. Without the last of those the run would measure an
    /// ordinary holiday and S6-11 would assert against an empty set.
    /// </summary>
    /// <param name="definition">Assembled scenario to check</param>
    private static IReadOnlyList<string> ValidateCrossMonthSetup(AutofillScenarioDefinition definition)
    {
        var problems = new List<string>();
        var employee = Scenario6SpecConstants.CrossMonthHolidayEmployee;
        var days = definition.Absences.DaysOf(employee);

        if (days.Count == 0 || days[0] != Scenario6SpecConstants.CrossMonthModelledFrom)
        {
            problems.Add(
                $"the modelled holiday of '{employee}' does not start on the first day of the period "
                + $"({Scenario6SpecConstants.CrossMonthModelledFrom:yyyy-MM-dd}); the February half must be absent "
                + "from the fixture because the engine never receives it, and the in-period half must be complete "
                + "from the very first day or the seam is not tested");
        }

        if (days.Count > 0 && days[^1] != Scenario6SpecConstants.CrossMonthHolidayUntil)
        {
            problems.Add(
                $"the modelled holiday of '{employee}' ends on {days[^1]:yyyy-MM-dd} instead of "
                + $"{Scenario6SpecConstants.CrossMonthHolidayUntil:yyyy-MM-dd}");
        }

        var carryIn = definition.OpenCarryIns.FirstOrDefault(c => string.Equals(
            c.AgentId, employee, StringComparison.Ordinal));
        if (carryIn is null)
        {
            problems.Add(
                $"'{employee}' carries no OPEN previous-month package, so the seam this run is built around does "
                + "not exist");
            return problems;
        }

        for (var offset = 0; offset < carryIn.MissingDays; offset++)
        {
            var owed = definition.PeriodFrom.AddDays(offset);
            if (!definition.Absences.Covers(employee, owed))
            {
                problems.Add(
                    $"the carried-in package of '{employee}' still owes {owed:yyyy-MM-dd}, which the holiday does "
                    + "not cover — the collision between carry-in and absence would then be partial and S6-11 would "
                    + "measure something else");
            }
        }

        return problems;
    }
}
