// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.Autofill.Analysis;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario5;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario6;

/// <summary>
/// Checks the scenario-6 fixture against the consistency conditions the specification states, before
/// any run. A broken setup has to fail as a setup error with a readable message; a red rule assertion
/// that in truth blames a fixture mistake is worse than no test at all.
/// <para>
/// Everything here is arithmetic over the declared rows — no plan is needed and none is read. The
/// checks are written PER WINDOW and never against the literal ten-and-four split of the main run:
/// the cross-month window of S6d is eight days with five working days and the calibration run has an
/// employee more, so a hard-coded split would pass S6a and silently mis-guard every other variant.
/// The literal split is checked additionally, and only where the specification states it.
/// </para>
/// </summary>
public static class Scenario6FixtureGuard
{
    private const string ProblemSeparator = " | ";

    /// <summary>
    /// Returns one message per problem; an empty result means the fixture matches the specification
    /// and the run may proceed.
    /// </summary>
    /// <param name="definition">Assembled scenario to check</param>
    public static IReadOnlyList<string> Validate(AutofillScenarioDefinition definition)
    {
        var problems = new List<string>(AutofillCarryInGuard.Validate(definition));
        CheckScenarioFiveInheritance(definition, problems);
        CheckAbsencesExist(definition, problems);
        CheckEveryRowIsOneDay(definition, problems);
        CheckCreditPerWindow(definition, problems);
        CheckNoAbsenceRowStacksOnAnother(definition, problems);
        return problems;
    }

    /// <summary>
    /// The extra conditions the two-week main window carries: exactly fourteen rows, exactly two
    /// Monday-to-Sunday weeks, ten paid and four unpaid rows and a credit of 81.82 h. They apply to
    /// every window that spans the specification's holiday dates and to no other, which is why they
    /// are a separate call the runs with a different window simply do not make.
    /// </summary>
    /// <param name="definition">Assembled scenario to check</param>
    /// <param name="employee">Employee whose two-week window is checked</param>
    public static IReadOnlyList<string> ValidateTwoWeekWindow(
        AutofillScenarioDefinition definition, string employee)
    {
        var problems = new List<string>();
        var windows = definition.Absences.WindowsOf(employee);
        if (windows.Count != 1)
        {
            problems.Add(
                $"'{employee}' carries {Count(windows.Count)} absence window(s); the specification states exactly "
                + "one, from 9 to 22 March.");
            return problems;
        }

        var (from, until) = windows[0];
        if (from != Scenario6SpecConstants.HolidayFrom || until != Scenario6SpecConstants.HolidayUntil)
        {
            problems.Add(
                $"The holiday of '{employee}' runs {from:yyyy-MM-dd}..{until:yyyy-MM-dd}; the specification states "
                + $"{Scenario6SpecConstants.HolidayFrom:yyyy-MM-dd}.."
                + $"{Scenario6SpecConstants.HolidayUntil:yyyy-MM-dd}.");
        }

        if (from.DayOfWeek != DayOfWeek.Monday || until.DayOfWeek != DayOfWeek.Sunday)
        {
            problems.Add(
                $"The holiday of '{employee}' starts on a {from.DayOfWeek} and ends on a {until.DayOfWeek}. The "
                + "specification states exactly two calendar weeks, Monday to Sunday — the whole ten-paid/four-unpaid "
                + "split follows from that shape and from nothing else.");
        }

        var rows = RowsOf(definition, employee);
        if (rows.Count != Scenario6SpecConstants.HolidayRows)
        {
            problems.Add(
                $"'{employee}' carries {Count(rows.Count)} absence row(s); the specification states "
                + $"{Count(Scenario6SpecConstants.HolidayRows)}, one per calendar day (owner decision O2).");
        }

        var paid = rows.Count(r => r.GrantsCredit);
        if (paid != Scenario6SpecConstants.HolidayPaidRows)
        {
            problems.Add(
                $"'{employee}' carries {Count(paid)} paid absence row(s); the specification states "
                + $"{Count(Scenario6SpecConstants.HolidayPaidRows)} — the ten Mondays to Fridays of two weeks. The "
                + "other four block their day and pay nothing.");
        }

        var credit = (double)definition.Absences.CreditHoursOf(employee);
        var expected = (double)Scenario6SpecConstants.WindowCreditHours;
        if (Math.Abs(credit - expected) > Scenario6SpecConstants.CreditTolerance)
        {
            problems.Add(
                $"The holiday of '{employee}' credits {Hours(credit)} h; the specification states {Hours(expected)} h "
                + $"= {Count(Scenario6SpecConstants.HolidayPaidRows)} x 180/22 h.");
        }

        return problems;
    }

    /// <summary>
    /// Observations the guard reports but does not fail on — facts about the fixture a phase-C reader
    /// needs in order to tell an algorithmic finding from an arithmetic one.
    /// </summary>
    /// <param name="definition">Assembled scenario to describe</param>
    public static IReadOnlyList<string> Notes(AutofillScenarioDefinition definition)
    {
        var notes = new List<string>();
        foreach (var window in definition.Absences.AllWindows())
        {
            var rows = definition.Absences.Rows
                .Where(r => string.Equals(r.AgentId, window.AgentId, StringComparison.Ordinal)
                            && r.Date >= window.From && r.Date <= window.Until)
                .ToList();
            notes.Add(
                $"absence window {window.AgentId} (rank {Count(definition.ListRankOf(window.AgentId))}) "
                + $"{window.From:yyyy-MM-dd}..{window.Until:yyyy-MM-dd}: {Count(rows.Count)} one-day rows, "
                + $"{Count(rows.Count(r => r.GrantsCredit))} of them paid, credit "
                + $"{Hours((double)rows.Sum(r => r.GrantsCredit ? r.Hours : 0m))} h");
        }

        foreach (var window in definition.Absences.AllWindows())
        {
            var reachable = AbsenceAnalyzer.MaxReachableShifts(window.AgentId, definition);
            var agent = definition.Context.Agents.FirstOrDefault(a => a.Id == window.AgentId);
            var target = agent?.GuaranteedHours ?? 0;
            var credit = (double)definition.Absences.CreditHoursOf(window.AgentId);
            var needed = Math.Max(0, target - credit - (agent?.CurrentHours ?? 0))
                / AutofillSpecConstants.ShiftHours;
            notes.Add(
                $"reachability of {window.AgentId}: the rhythm allows at most {Count(reachable)} further shift(s) on "
                + $"the days no absence and no FREE command closes, and {Hours(needed)} shift(s) are needed to reach "
                + $"{Hours(target)} h once the credit of {Hours(credit)} h is counted. This is an UPPER bound — it "
                + "ignores coverage, rotation and the package already in progress — so a plan reaching it is not "
                + "expected; a target above it would be provably unreachable and any shortfall forced.");
        }

        if (definition.Absences.Rows.Any(r => r.Date == AutofillSpecConstants.PeriodFrom))
        {
            notes.Add(
                "An absence covers the first day of the period. A window that began in the previous month exists in "
                + "the fixture only from the period start on: an absence is a set of day rows and the Api's blocker "
                + "query filters them to the period, so the part before it is structurally invisible to the engine "
                + "and is documented rather than simulated.");
        }

        return notes;
    }

    /// <summary>Joins problems into one readable line.</summary>
    /// <param name="problems">Problem messages</param>
    public static string Describe(IReadOnlyList<string> problems) => string.Join(ProblemSeparator, problems);

    /// <summary>
    /// Proves the scenario-6 fixture still IS the scenario-5 fixture. Everything the scenario-6
    /// assertions read from the scenario-5 run as their control depends on it: same orders, same
    /// slots, same previous month, same commands, same preferences. Only the roster may grow, and only
    /// by the eighteenth employee of the calibration run.
    /// </summary>
    /// <param name="definition">Assembled scenario to check</param>
    /// <param name="problems">Collector for problem messages</param>
    private static void CheckScenarioFiveInheritance(
        AutofillScenarioDefinition definition, List<string> problems)
    {
        if (definition.Context.Shifts.Count != Scenario5SpecConstants.TotalRequiredShifts)
        {
            problems.Add(
                $"The scenario demands {Count(definition.Context.Shifts.Count)} shifts; scenario 6 inherits the "
                + $"{Count(Scenario5SpecConstants.TotalRequiredShifts)} of scenario 5 unchanged.");
        }

        if (definition.SlotsPerDay != Scenario5SpecConstants.AssignmentsPerDay)
        {
            problems.Add(
                $"A day holds {Count(definition.SlotsPerDay)} slots; scenario 6 inherits the "
                + $"{Count(Scenario5SpecConstants.AssignmentsPerDay)} of scenario 5.");
        }

        if (definition.CarryIns.Count != Scenario5SpecConstants.OpenCarryInCount
            + Scenario5SpecConstants.ClosedCarryInCount)
        {
            problems.Add(
                $"The scenario declares {Count(definition.CarryIns.Count)} previous-month packages; scenario 6 "
                + "inherits the seventeen of scenario 5 unchanged.");
        }

        if (definition.ScheduleCommands is null || definition.ScheduleCommands.Commands.Count == 0)
        {
            problems.Add("The scenario carries no keyword window; scenario 6 inherits the five of scenario 5.");
        }

        if (definition.ShiftPreferences is null || definition.ShiftPreferences.IsEmpty)
        {
            problems.Add("The scenario carries no shift preference; scenario 6 inherits those of scenario 5.");
        }

        var expectedRoster = definition.EmployeesInListOrder.Count == Scenario5SpecConstants.EmployeeCount
            || definition.EmployeesInListOrder.Count == Scenario6SpecConstants.CalibrationEmployeeCount;
        if (!expectedRoster)
        {
            problems.Add(
                $"The roster holds {Count(definition.EmployeesInListOrder.Count)} employees; scenario 6 plans with "
                + $"{Count(Scenario5SpecConstants.EmployeeCount)}, or "
                + $"{Count(Scenario6SpecConstants.CalibrationEmployeeCount)} in the calibration run.");
        }
    }

    private static void CheckAbsencesExist(AutofillScenarioDefinition definition, List<string> problems)
    {
        if (definition.Absences.Rows.Count == 0)
        {
            problems.Add(
                "The scenario books no absence at all. Every scenario-6 run does — a run without one is scenario 5 "
                + "and would measure the absence rules against an empty set.");
        }
    }

    /// <summary>
    /// Proves owner decision O2 held: every row is ONE day. The check reaches into the engine context
    /// rather than trusting the fixture object, because the context is what the engine receives, and a
    /// blocker spanning several days would take the day multiplication in the credit computation that
    /// production never reaches.
    /// </summary>
    /// <param name="definition">Assembled scenario to check</param>
    /// <param name="problems">Collector for problem messages</param>
    private static void CheckEveryRowIsOneDay(AutofillScenarioDefinition definition, List<string> problems)
    {
        foreach (var blocker in definition.Context.BreakBlockers.Where(b => b.FromInclusive != b.UntilInclusive))
        {
            problems.Add(
                $"The blocker of '{blocker.AgentId}' spans {blocker.FromInclusive:yyyy-MM-dd}.."
                + $"{blocker.UntilInclusive:yyyy-MM-dd}. Owner decision O2 states one-day rows, because production "
                + "builds one blocker per Break row and the credit computation multiplies the hours by the number of "
                + "days a blocker spans — a range would multiply the credit on a path production never reaches.");
        }

        var contextRows = definition.Context.BreakBlockers.Count;
        if (contextRows != definition.Absences.Rows.Count)
        {
            problems.Add(
                $"The engine receives {Count(contextRows)} blockers while the fixture declares "
                + $"{Count(definition.Absences.Rows.Count)} rows. The measurement would then judge against a "
                + "different absence list than the one the engine planned with.");
        }
    }

    /// <summary>
    /// Checks the credit of EVERY window against its own shape: the paid rows must be exactly the
    /// contractual working days of that window, the unpaid rows exactly its weekend days, and each
    /// paid row must carry the daily reference value EXACTLY — not merely close to it. The identity
    /// check is the point: 8.1818 also sums to within a hundredth of the expected credit, so a sum
    /// tolerance alone cannot tell the reference value from a rounded literal.
    /// </summary>
    /// <param name="definition">Assembled scenario to check</param>
    /// <param name="problems">Collector for problem messages</param>
    private static void CheckCreditPerWindow(AutofillScenarioDefinition definition, List<string> problems)
    {
        foreach (var window in definition.Absences.AllWindows())
        {
            var rows = definition.Absences.Rows
                .Where(r => string.Equals(r.AgentId, window.AgentId, StringComparison.Ordinal)
                            && r.Date >= window.From && r.Date <= window.Until)
                .ToList();

            var expectedDays = window.Until.DayNumber - window.From.DayNumber + 1;
            if (rows.Count != expectedDays)
            {
                problems.Add(
                    $"The window {window.AgentId} {window.From:yyyy-MM-dd}..{window.Until:yyyy-MM-dd} spans "
                    + $"{Count(expectedDays)} days but holds {Count(rows.Count)} rows. Every calendar day of an "
                    + "absence needs a row of its own, or the plan may staff the gap.");
            }

            var expectedPaid = rows.Count(r => Scenario6Fixture.IsContractualWorkingDay(r.Date));
            var actualPaid = rows.Count(r => r.GrantsCredit);
            if (actualPaid != expectedPaid)
            {
                problems.Add(
                    $"The window {window.AgentId} {window.From:yyyy-MM-dd}..{window.Until:yyyy-MM-dd} pays "
                    + $"{Count(actualPaid)} of its {Count(rows.Count)} rows; its contractual working days are "
                    + $"{Count(expectedPaid)}. The macro reference credits Monday to Friday and nothing else, "
                    + "because the daily value divides the monthly guarantee by the month's working days.");
                continue;
            }

            foreach (var row in rows.Where(r => r.GrantsCredit
                                                && r.Hours != Scenario6SpecConstants.DailyCreditHours))
            {
                problems.Add(
                    $"The absence row of '{row.AgentId}' on {row.Date:yyyy-MM-dd} carries {row.Hours} h. The daily "
                    + "reference value is 180/22 h constructed as a decimal division; a rounded literal is a "
                    + "different number that a sum tolerance would not catch.");
            }

            var expectedCredit = expectedPaid * (double)Scenario6SpecConstants.DailyCreditHours;
            var actualCredit = (double)rows.Where(r => r.GrantsCredit).Sum(r => r.Hours);
            if (Math.Abs(actualCredit - expectedCredit) > Scenario6SpecConstants.CreditTolerance)
            {
                problems.Add(
                    $"The window {window.AgentId} {window.From:yyyy-MM-dd}..{window.Until:yyyy-MM-dd} credits "
                    + $"{Hours(actualCredit)} h; its {Count(expectedPaid)} working days credit "
                    + $"{Hours(expectedCredit)} h.");
            }
        }
    }

    /// <summary>
    /// Proves no two absence rows share a date. The engine accumulates break hours per AGENT without
    /// keying on the date, so a stacked day is credited twice while blocking once — and the data model
    /// explicitly allows two absences on one day, which makes this a real trap rather than a
    /// theoretical one.
    /// </summary>
    /// <param name="definition">Assembled scenario to check</param>
    /// <param name="problems">Collector for problem messages</param>
    private static void CheckNoAbsenceRowStacksOnAnother(
        AutofillScenarioDefinition definition, List<string> problems)
    {
        foreach (var group in definition.Context.BreakBlockers
                     .GroupBy(b => (b.AgentId, b.FromInclusive))
                     .Where(g => g.Count() > 1))
        {
            problems.Add(
                $"'{group.Key.AgentId}' carries {Count(group.Count())} blockers on "
                + $"{group.Key.FromInclusive:yyyy-MM-dd}; the credit of that day would be counted once per blocker.");
        }
    }

    private static IReadOnlyList<AutofillBreakBlocker> RowsOf(
        AutofillScenarioDefinition definition, string employee)
        => definition.Absences.Rows
            .Where(r => string.Equals(r.AgentId, employee, StringComparison.Ordinal))
            .ToList();

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Hours(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
