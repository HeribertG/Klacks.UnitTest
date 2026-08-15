// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario4;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario5;

/// <summary>
/// The cheap half of scenario 5: everything about the fixture that can be proven without running the
/// engine. These tests are NOT explicit — they cost milliseconds, and they are the ones that catch a
/// broken fixture before anybody spends minutes of engine time on it.
/// </summary>
[TestFixture]
[Category("Autofill")]
[Category("Scenario5")]
public class Scenario5FixtureGuardTests
{
    [Test]
    public void TheDayShiftSpanIsClassifiedAsLate()
    {
        var problems = AutofillShiftCatalog.ValidateDayShiftInference();

        problems.ShouldBeEmpty(
            "The whole scenario-5 modelling rests on this single fact: the engine has no fourth shift class and "
            + "classifies the day shift's span as late, which is why the day shift is a slot with an id of its own and "
            + "why every rotation, purity and fairness expectation of this scenario is written for a three-class view. "
            + $"{string.Join(" | ", problems)}");

        TestContext.Out.WriteLine(
            $"day shift {AutofillSpecConstants.DayStartTime}-{AutofillSpecConstants.DayEndTime} is engine shift class "
            + $"{AutofillShiftCatalog.ShiftClassOf(AutofillShiftKind.Day)}");
    }

    [Test]
    public void EveryPinnedIdIncludingTheDayShiftResolvesBackToItsOrderAndKind()
    {
        var problems = new List<string>(AutofillShiftCatalog.ValidateOrderIdOrdering());
        for (var order = AutofillShiftCatalog.FirstOrderIndex;
             order < AutofillShiftCatalog.FirstOrderIndex + Scenario5SpecConstants.OrderCount;
             order++)
        {
            foreach (var kind in AutofillShiftCatalog.SlotKindsOf(includeDayShift: true))
            {
                var id = AutofillShiftCatalog.ShiftIdOf(order, kind);
                if (AutofillShiftCatalog.OrderOf(id) != order)
                {
                    problems.Add(
                        $"order {order.ToString(CultureInfo.InvariantCulture)} / {kind} resolves back to order "
                        + $"{AutofillShiftCatalog.OrderOf(id).ToString(CultureInfo.InvariantCulture)}.");
                }

                if (AutofillShiftCatalog.SlotKindOf(id) != kind)
                {
                    problems.Add(
                        $"order {order.ToString(CultureInfo.InvariantCulture)} / {kind} resolves back to slot kind "
                        + $"{AutofillShiftCatalog.SlotKindOf(id)}.");
                }
            }
        }

        problems.ShouldBeEmpty(
            "The day shift and the late shift carry the SAME engine class, so the shift reference id is the only thing "
            + "that separates them: every day-shift metric, the plan matrix and the blacklist check resolve through it. "
            + $"An id that does not resolve back makes all three measure the wrong slot. {string.Join(" | ", problems)}");

        TestContext.Out.WriteLine(
            "auction order inside one day: "
            + AutofillShiftCatalog.DescribeAuctionOrderOfOneDay(
                Scenario5SpecConstants.OrderCount, includeDayShift: true));
    }

    [Test]
    public void TheMainFixturePassesEveryConsistencyCondition()
    {
        var definition = Scenario5Fixture.BuildMainRun();

        var problems = Scenario5FixtureGuard.Validate(definition);

        foreach (var note in Scenario5FixtureGuard.Notes(definition))
        {
            TestContext.Out.WriteLine("fixture note: " + note);
        }

        problems.ShouldBeEmpty(
            "The scenario-5 fixture must satisfy every consistency condition of the specification before a single "
            + $"engine second is spent on it: {string.Join(" | ", problems)}");
    }

    [Test]
    public void TheControlFixturePassesEveryConsistencyConditionAsWell()
    {
        var definition = Scenario5Fixture.BuildControlRun();

        var problems = Scenario5FixtureGuard.Validate(definition);

        problems.ShouldBeEmpty(
            "The control run S5b differs from the main run in exactly two inputs — no preferences and no keyword "
            + "windows — and in nothing else, so it must pass the same conditions. A control run that differed in a "
            + $"third way would attribute its differences to the wrong cause: {string.Join(" | ", problems)}");
    }

    [Test]
    public void TheControlRunCarriesNeitherPreferencesNorCommandsAndIsOtherwiseIdentical()
    {
        var main = Scenario5Fixture.BuildMainRun();
        var control = Scenario5Fixture.BuildControlRun();

        var problems = new List<string>();
        if (control.Context.ShiftPreferences.Count != 0)
        {
            problems.Add(
                $"the control run carries "
                + $"{control.Context.ShiftPreferences.Count.ToString(CultureInfo.InvariantCulture)} preference(s)");
        }

        if (control.Context.ScheduleCommands.Count != 0)
        {
            problems.Add(
                $"the control run carries "
                + $"{control.Context.ScheduleCommands.Count.ToString(CultureInfo.InvariantCulture)} command(s)");
        }

        if (main.Context.Shifts.Count != control.Context.Shifts.Count)
        {
            problems.Add("the two runs demand a different number of shifts");
        }

        if (!main.EmployeesInListOrder.SequenceEqual(control.EmployeesInListOrder, StringComparer.Ordinal))
        {
            problems.Add("the two runs use a different roster");
        }

        if (!main.CarryIns.SequenceEqual(control.CarryIns))
        {
            problems.Add("the two runs use a different previous month");
        }

        if (main.Config.RandomSeed != control.Config.RandomSeed)
        {
            problems.Add("the two runs use a different seed");
        }

        problems.ShouldBeEmpty(
            "S5b isolates the effect of the preferences and the keyword windows, which only works while it differs "
            + $"from S5a in those two inputs and in nothing else. {string.Join(" | ", problems)}");

        TestContext.Out.WriteLine(
            $"S5a carries {main.Context.ShiftPreferences.Count.ToString(CultureInfo.InvariantCulture)} preference "
            + $"row(s) and {main.Context.ScheduleCommands.Count.ToString(CultureInfo.InvariantCulture)} command row(s); "
            + "S5b carries none of either.");
    }

    /// <summary>
    /// The specification requires the first fifteen previous-month packages to be the scenario-4
    /// tables UNCHANGED. Comparing the two assembled fixtures rather than the two hand-written tables
    /// is what makes this meaningful: it is the built carry-ins the engine and the analyzer see, and a
    /// later edit to either scenario's table shows up here instead of silently drifting apart.
    /// </summary>
    [Test]
    public void TheFirstFifteenCarryInsAreTheScenarioFourTablesUnchanged()
    {
        var scenario4 = Scenario4CarryInFixture.BuildMainRun().CarryIns
            .Where(c => Scenario5SpecConstants.InheritedEmployees.Contains(c.AgentId, StringComparer.Ordinal))
            .OrderBy(c => c.AgentId, StringComparer.Ordinal)
            .ToList();
        var scenario5 = Scenario5Fixture.BuildMainRun().CarryIns
            .Where(c => Scenario5SpecConstants.InheritedEmployees.Contains(c.AgentId, StringComparer.Ordinal))
            .OrderBy(c => c.AgentId, StringComparer.Ordinal)
            .ToList();

        var problems = new List<string>();
        if (scenario4.Count != Scenario5SpecConstants.InheritedEmployees.Count)
        {
            problems.Add(
                $"scenario 4 supplies {scenario4.Count.ToString(CultureInfo.InvariantCulture)} of the "
                + $"{Scenario5SpecConstants.InheritedEmployees.Count.ToString(CultureInfo.InvariantCulture)} inherited "
                + "packages");
        }

        if (scenario5.Count != Scenario5SpecConstants.InheritedEmployees.Count)
        {
            problems.Add(
                $"scenario 5 supplies {scenario5.Count.ToString(CultureInfo.InvariantCulture)} of them");
        }

        for (var i = 0; i < Math.Min(scenario4.Count, scenario5.Count); i++)
        {
            if (scenario4[i] != scenario5[i])
            {
                problems.Add($"{scenario4[i].AgentId}: scenario 4 has {scenario4[i]}, scenario 5 has {scenario5[i]}");
            }
        }

        problems.ShouldBeEmpty(
            "MA-01 to MA-15 must carry exactly the previous-month packages of specification scenario 4, so that every "
            + "difference between the two scenarios' results is caused by the day shifts, the two extra employees, the "
            + $"preferences or the keyword windows — and by nothing else. {string.Join(" | ", problems)}");
    }

    /// <summary>
    /// The preference list must name shift IDS, one per order, and never a class. The engine compares
    /// Guids; a fixture that expressed "all night shifts" once would leave two of the three night
    /// shifts open and the blacklist assertion would then pass or fail for the wrong reason.
    /// </summary>
    [Test]
    public void TheBlacklistCoversTheNightShiftOfEveryOrderForBothPreferringEmployees()
    {
        var definition = Scenario5Fixture.BuildMainRun();
        var preferences = definition.ShiftPreferences.ShouldNotBeNull();

        var problems = new List<string>();
        foreach (var employee in Scenario5SpecConstants.DayShiftEmployees)
        {
            for (var order = AutofillShiftCatalog.FirstOrderIndex;
                 order < AutofillShiftCatalog.FirstOrderIndex + Scenario5SpecConstants.OrderCount;
                 order++)
            {
                var night = AutofillShiftCatalog.ShiftIdOf(order, AutofillShiftKind.Night);
                if (!preferences.IsBlacklisted(employee, night))
                {
                    problems.Add(
                        $"{employee} is not blacklisted from the night shift of order "
                        + order.ToString(CultureInfo.InvariantCulture));
                }

                var day = AutofillShiftCatalog.ShiftIdOf(order, AutofillShiftKind.Day);
                if (!preferences.IsPreferred(employee, day))
                {
                    problems.Add(
                        $"{employee} does not prefer the day shift of order "
                        + order.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        problems.ShouldBeEmpty(
            "The engine binds a preference to a shift reference id and knows nothing about shift classes, so "
            + "\"all night shifts\" has to be stated once per order. A missing entry would leave that order's night "
            + $"shift open to a blacklisted employee. {string.Join(" | ", problems)}");

        preferences.EmployeesWith(ShiftPreferenceKind.Blacklist)
            .ShouldBe(Scenario5SpecConstants.DayShiftEmployees, ignoreOrder: false);
    }

    /// <summary>
    /// The night cohort is fifteen and not seventeen, and the specification's per-head figure depends
    /// on it. Derived here from the preference list rather than pinned as a bare number, so a change to
    /// the blacklist moves the cohort with it.
    /// </summary>
    [Test]
    public void TheNightPoolIsFifteenEmployees()
    {
        var definition = Scenario5Fixture.BuildMainRun();
        var preferences = definition.ShiftPreferences.ShouldNotBeNull();

        var pool = definition.EmployeesInListOrder
            .Where(e => !AutofillShiftCatalog.SlotKindsOf(includeDayShift: true)
                .Where(k => k == AutofillShiftKind.Night)
                .SelectMany(k => Enumerable
                    .Range(AutofillShiftCatalog.FirstOrderIndex, Scenario5SpecConstants.OrderCount)
                    .Select(o => AutofillShiftCatalog.ShiftIdOf(o, k)))
                .Any(id => preferences.IsBlacklisted(e, id)))
            .ToList();

        pool.Count.ShouldBe(
            Scenario5SpecConstants.NightPoolSize,
            $"{Scenario5SpecConstants.TotalNightShifts.ToString(CultureInfo.InvariantCulture)} night shifts have to be "
            + $"spread over the employees who may take one. The fixture leaves "
            + $"{pool.Count.ToString(CultureInfo.InvariantCulture)} eligible: {string.Join(", ", pool)}");

        TestContext.Out.WriteLine(
            $"night pool {pool.Count.ToString(CultureInfo.InvariantCulture)} employees for "
            + $"{Scenario5SpecConstants.TotalNightShifts.ToString(CultureInfo.InvariantCulture)} night shifts = "
            + (Scenario5SpecConstants.TotalNightShifts / (double)pool.Count).ToString("0.0", CultureInfo.InvariantCulture)
            + " per head");
    }

    /// <summary>
    /// The keyword windows must reach the engine as one row per employee and day. A window that
    /// expanded to nothing, or to the wrong days, would constrain nothing while still looking present
    /// in the fixture.
    /// </summary>
    [Test]
    public void TheKeywordWindowsExpandToOneEngineRowPerDay()
    {
        var definition = Scenario5Fixture.BuildMainRun();
        var commands = definition.ScheduleCommands.ShouldNotBeNull();

        var expected = commands.Commands.Sum(c => c.LengthDays);
        definition.Context.ScheduleCommands.Count.ShouldBe(
            expected,
            "Every command window has to reach the engine as one CoreScheduleCommand per calendar day; the engine's "
            + "record carries a single date and no window of its own.");

        var problems = new List<string>();
        foreach (var command in commands.Commands)
        {
            foreach (var day in command.Days())
            {
                if (!definition.Context.ScheduleCommands.Any(
                        c => string.Equals(c.AgentId, command.AgentId, StringComparison.Ordinal)
                            && c.Date == day
                            && c.Keyword == command.Keyword))
                {
                    problems.Add($"{command.AgentId} {command.Keyword} {day:yyyy-MM-dd} is missing");
                }
            }
        }

        problems.ShouldBeEmpty(
            $"The five windows of the specification must be present day by day: {string.Join(" | ", problems)}");

        TestContext.Out.WriteLine(
            "keyword windows: "
            + string.Join(
                ", ",
                commands.Commands.Select(c =>
                    $"{c.AgentId} {c.Keyword} {c.FromInclusive:MM-dd}..{c.UntilInclusive:MM-dd}")));
    }

    /// <summary>
    /// The framework values the specification asks the test to compute and log BEFORE any run. They are
    /// arithmetic over the fixture, so they belong in the cheap half; phase C reads them next to the
    /// measured result instead of recomputing them from a report.
    /// </summary>
    [Test]
    public void TheArithmeticFramingIsComputedAndLogged()
    {
        var definition = Scenario5Fixture.BuildMainRun();

        var problems = new List<string>();
        if (definition.Context.Shifts.Count != Scenario5SpecConstants.TotalRequiredShifts)
        {
            problems.Add(
                $"the fixture demands {definition.Context.Shifts.Count.ToString(CultureInfo.InvariantCulture)} shifts, "
                + $"not {Scenario5SpecConstants.TotalRequiredShifts.ToString(CultureInfo.InvariantCulture)}");
        }

        var employeeDays = definition.EmployeesInListOrder.Count * AutofillSpecConstants.PeriodDays;
        if (employeeDays != Scenario5SpecConstants.TotalEmployeeDays)
        {
            problems.Add(
                $"the roster offers {employeeDays.ToString(CultureInfo.InvariantCulture)} employee days, not "
                + Scenario5SpecConstants.TotalEmployeeDays.ToString(CultureInfo.InvariantCulture));
        }

        if (Scenario5SpecConstants.RequiredWorkShare >= Scenario5SpecConstants.IdealFiveTwoWorkShare)
        {
            problems.Add(
                "the work share is not below the 5/2 ideal, so the specification's expectation that a clean "
                + "five-work/two-free rhythm is just reachable no longer holds");
        }

        problems.ShouldBeEmpty(
            $"The framing every scenario-5 result is read against must hold: {string.Join(" | ", problems)}");

        TestContext.Out.WriteLine(
            $"places {employeeDays.ToString(CultureInfo.InvariantCulture)}, demand "
            + $"{Scenario5SpecConstants.TotalRequiredShifts.ToString(CultureInfo.InvariantCulture)}, work share "
            + (Scenario5SpecConstants.RequiredWorkShare * 100).ToString("0.0", CultureInfo.InvariantCulture)
            + " % against the 5/2 ideal of "
            + (Scenario5SpecConstants.IdealFiveTwoWorkShare * 100).ToString("0.00", CultureInfo.InvariantCulture)
            + " % — 5/2 is just reachable, free-block mode expected "
            + Scenario5SpecConstants.ExpectedFreeBlockMode.ToString(CultureInfo.InvariantCulture));
        TestContext.Out.WriteLine(
            $"hours offered {Scenario5SpecConstants.OfferedHours.ToString("0", CultureInfo.InvariantCulture)} h, "
            + $"demanded {Scenario5SpecConstants.TotalRequiredHours.ToString("0", CultureInfo.InvariantCulture)} h = "
            + (Scenario5SpecConstants.TotalRequiredHours / Scenario5SpecConstants.OfferedHours * 100)
                .ToString("0.00", CultureInfo.InvariantCulture)
            + " %");
        TestContext.Out.WriteLine(
            "top-down reference (a) full guarantee costs "
            + $"{Scenario5SpecConstants.FullGuaranteeShifts.ToString(CultureInfo.InvariantCulture)} shifts = "
            + $"{Scenario5SpecConstants.FullGuaranteeHours.ToString("0", CultureInfo.InvariantCulture)} h, so "
            + $"{Scenario5SpecConstants.FullGuaranteeRanks.ToString(CultureInfo.InvariantCulture)} ranks reach it and "
            + $"rank {Scenario5SpecConstants.EmployeeCount.ToString(CultureInfo.InvariantCulture)} keeps the remaining "
            + $"{Scenario5SpecConstants.RemainderRankShifts.ToString(CultureInfo.InvariantCulture)} shifts");
        TestContext.Out.WriteLine(
            "top-down reference (b) flat share "
            + (Scenario5SpecConstants.TotalRequiredShifts / (double)Scenario5SpecConstants.EmployeeCount)
                .ToString("0.0", CultureInfo.InvariantCulture)
            + $" shifts = {Scenario5SpecConstants.FlatShareHours.ToString("0.0", CultureInfo.InvariantCulture)} h for "
            + "every rank");
        TestContext.Out.WriteLine(
            $"day shifts {Scenario5SpecConstants.TotalDayShifts.ToString(CultureInfo.InvariantCulture)}; the two "
            + "preferring employees can work about "
            + $"{Scenario5SpecConstants.FlatShareShifts.ToString(CultureInfo.InvariantCulture)} shifts each under 5/2, "
            + "so at least "
            + (Scenario5SpecConstants.TotalDayShifts - (2 * Scenario5SpecConstants.FlatShareShifts))
                .ToString(CultureInfo.InvariantCulture)
            + " day shifts necessarily go to employees who never asked for one — not a preference violation");
    }
}
