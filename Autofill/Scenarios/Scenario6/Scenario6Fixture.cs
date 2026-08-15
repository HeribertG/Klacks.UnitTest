// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario5;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario6;

/// <summary>
/// Builds the eight runs of specification scenario 6: the scenario-5 fixture, unchanged in every
/// respect, plus booked holidays. Nothing else moves — same orders, same roster, same previous month,
/// same keyword windows, same preferences — because the whole scenario is a controlled experiment
/// against the finished scenario-5 run and a second changed input would destroy the attribution.
/// <para>
/// THE HOLIDAY IS FOURTEEN ONE-DAY ROWS, never one range. That is owner decision O2 and it mirrors
/// production exactly: a <c>Break</c> row holds a single <c>CurrentDate</c>, and the Api turns each of
/// them into a blocker whose start and end are that day. The alternative — one blocker spanning two
/// weeks — would take a multiplication in the credit computation that production never reaches and
/// pin the hour assertion to it.
/// </para>
/// <para>
/// THE HOURS ARE A SIMULATED MACRO OUTPUT and are documented as one, not as a rule the fixture
/// discovered. Owner decision O3: the reference semantics credit only contractual working days, and
/// the daily value is the monthly guarantee over the month's 22 working days. A weekend row therefore
/// carries zero hours — and it is STILL a row, because the day must stay blocked: the engine's credit
/// skip for hourless blockers drops the hours and never the block.
/// </para>
/// </summary>
public static class Scenario6Fixture
{
    /// <summary>S6a — scenario 5 with MA-16 on holiday for the two weeks 9 to 22 March.</summary>
    public static AutofillScenarioDefinition BuildMainRun()
        => BuildCore(
            HolidayOf(Scenario6SpecConstants.HolidayEmployee),
            Scenario5SpecConstants.Employees,
            lockedWorks: null);

    /// <summary>
    /// S6b — the calibration run: S6a plus an eighteenth employee at 165 guaranteed hours, without
    /// keywords and without preferences. He raises the roster to 18 x 31 minus the 14 absent days =
    /// 544 places for the same 372 assignments, a work share of 68.4 % against the 71.43 % a clean
    /// five-work/two-free rhythm carries — so the rhythm becomes reachable again and the run answers
    /// whether the engine takes it when it is available.
    /// </summary>
    public static AutofillScenarioDefinition BuildCalibrationRun()
        => BuildCore(
            HolidayOf(Scenario6SpecConstants.HolidayEmployee),
            [.. Scenario5SpecConstants.Employees, Scenario6SpecConstants.EighteenthEmployee],
            lockedWorks: null);

    /// <summary>
    /// S6c — the holiday on list rank 1. Two things at once, and neither can be read off the other:
    /// the top-down rule serves rank 1 first, so an absence there must break the top-down picture
    /// visibly; and MA-01's FREE window of 10 to 12 March lies entirely inside the holiday, so two
    /// independent hard blocks point at the same three days and must produce one blocked day, never a
    /// conflict.
    /// </summary>
    public static AutofillScenarioDefinition BuildTopRankRun()
        => BuildCore(
            HolidayOf(Scenario6SpecConstants.TopRankHolidayEmployee),
            Scenario5SpecConstants.Employees,
            lockedWorks: null);

    /// <summary>
    /// S6d — the holiday across the month boundary, 23 February to 8 March.
    /// <para>
    /// Only the in-period part exists as rows. That is not a simplification, it is the structure: an
    /// absence is a set of day rows and the Api's blocker query filters them to the period, so the
    /// February half is invisible to the engine by construction and cannot be modelled without
    /// modelling something the engine never receives. The February half is DOCUMENTED here and in the
    /// guard note instead, and what the run measures is the seam: MA-04 carries an open previous-month
    /// package that owes four more days, and the absence takes them.
    /// </para>
    /// </summary>
    public static AutofillScenarioDefinition BuildCrossMonthRun()
        => BuildCore(
            HolidayRows(
                Scenario6SpecConstants.CrossMonthHolidayEmployee,
                Scenario6SpecConstants.CrossMonthModelledFrom,
                Scenario6SpecConstants.CrossMonthHolidayUntil),
            Scenario5SpecConstants.Employees,
            lockedWorks: null);

    /// <summary>
    /// S6e — the holiday that swallows a keyword window. MA-03's OnlyEarly window runs 18 to 24 March
    /// and the holiday 16 to 29 March covers it completely, so the keyword can no longer narrow
    /// anything. It must produce no exception, no warning and no assignment: a command that constrains
    /// an empty set is not an error, it is simply without effect.
    /// </summary>
    public static AutofillScenarioDefinition BuildKeywordCoveredRun()
        => BuildCore(
            HolidayRows(
                Scenario6SpecConstants.KeywordHolidayEmployee,
                Scenario6SpecConstants.KeywordHolidayFrom,
                Scenario6SpecConstants.KeywordHolidayUntil),
            Scenario5SpecConstants.Employees,
            lockedWorks: null);

    /// <summary>
    /// S6f — both preference employees on holiday at once. Fifteen employees remain for 12 assignments
    /// a day inside the window, and every one of the 42 day shifts in it necessarily goes to somebody
    /// who never asked for one. The run's question is whether full coverage survives that.
    /// </summary>
    public static AutofillScenarioDefinition BuildBothPreferenceRun()
        => BuildCore(
            [
                .. HolidayOf(Scenario6SpecConstants.HolidayEmployee),
                .. HolidayOf(Scenario6SpecConstants.SecondPreferenceEmployee),
            ],
            Scenario5SpecConstants.Employees,
            lockedWorks: null);

    /// <summary>
    /// S6g — the provably unsolvable run: six employees on holiday at the same time leave eleven for
    /// twelve assignments a day. It cannot be planned, and the point is exactly that: the engine has to
    /// REPORT the infeasibility and must not buy coverage by breaking a hard rule.
    /// </summary>
    public static AutofillScenarioDefinition BuildUnsolvableRun()
        => BuildCore(
            [.. Scenario6SpecConstants.UnsolvableRunEmployees.SelectMany(HolidayOf)],
            Scenario5SpecConstants.Employees,
            lockedWorks: null);

    /// <summary>S6h — S6a again, this time reached through a replanning run over a frozen prefix.</summary>
    /// <param name="lockedWorks">Result of <c>FrozenPrefix.BuildLockedWorks</c> over the S6a plan</param>
    public static AutofillScenarioDefinition BuildReplanRun(IReadOnlyList<CoreLockedWork> lockedWorks)
        => BuildCore(
            HolidayOf(Scenario6SpecConstants.HolidayEmployee),
            Scenario5SpecConstants.Employees,
            lockedWorks);

    /// <summary>
    /// The fourteen rows of one employee's two-week holiday, 9 to 22 March.
    /// </summary>
    /// <param name="employee">Employee going on holiday</param>
    public static IReadOnlyList<AutofillBreakBlocker> HolidayOf(string employee)
        => HolidayRows(employee, Scenario6SpecConstants.HolidayFrom, Scenario6SpecConstants.HolidayUntil);

    /// <summary>
    /// One row per calendar day of a holiday window, with the hours the macro reference produces:
    /// the daily value on a contractual working day, nothing on a Saturday or Sunday. A weekend row
    /// exists nonetheless, because the day is blocked either way and leaving it out would let the plan
    /// staff a Saturday in the middle of a holiday.
    /// </summary>
    /// <param name="employee">Employee going on holiday</param>
    /// <param name="from">First absent day</param>
    /// <param name="until">Last absent day</param>
    public static IReadOnlyList<AutofillBreakBlocker> HolidayRows(string employee, DateOnly from, DateOnly until)
    {
        var rows = new List<AutofillBreakBlocker>();
        for (var date = from; date <= until; date = date.AddDays(1))
        {
            rows.Add(new AutofillBreakBlocker(
                employee,
                date,
                Scenario6SpecConstants.HolidayReason,
                IsContractualWorkingDay(date) ? Scenario6SpecConstants.DailyCreditHours : 0m));
        }

        return rows;
    }

    /// <summary>
    /// Whether the macro reference credits that day. It is Monday to Friday: the divisor of the daily
    /// value is the month's 22 working days, so crediting a weekend too would credit 31 days against a
    /// 22-day divisor and hand out half a month of hours too many.
    /// </summary>
    /// <param name="date">Calendar day to judge</param>
    public static bool IsContractualWorkingDay(DateOnly date)
        => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    /// <param name="absences">Holiday rows of this run</param>
    /// <param name="employees">Roster of this run, in list order</param>
    /// <param name="lockedWorks">In-period locked works of a replanning run; null for a fresh start</param>
    private static AutofillScenarioDefinition BuildCore(
        IReadOnlyList<AutofillBreakBlocker> absences,
        IReadOnlyList<string> employees,
        IReadOnlyList<CoreLockedWork>? lockedWorks)
    {
        var builder = new AutofillScenarioBuilder()
            .WithPeriod(AutofillSpecConstants.PeriodFrom, AutofillSpecConstants.PeriodUntil)
            .WithOrders(Scenario5SpecConstants.OrderCount)
            .WithDayShiftPerOrder()
            .WithGaParameters(Scenario6GaParameters.PopulationSize, Scenario6GaParameters.MaxGenerations);

        foreach (var employee in employees)
        {
            builder.AddEmployee(employee, GuaranteedHoursOf(employee));
        }

        builder.WithCarryIn([.. Scenario5Fixture.BuildCarryIns()]);
        builder.WithShiftPreferences(Scenario5Fixture.BuildPreferences());
        builder.WithScheduleCommands(Scenario5Fixture.BuildScheduleCommands());
        builder.WithBreakBlockers(new AutofillBreakBlockerInput(absences));

        if (lockedWorks is not null)
        {
            builder.WithLockedWorks(lockedWorks);
        }

        return builder.Build();
    }

    /// <summary>
    /// Guaranteed hours of one employee. Everybody carries the scenario-5 value; only the eighteenth
    /// employee of the calibration run carries the lower one the specification names for him.
    /// </summary>
    /// <param name="employee">Employee identifier</param>
    private static double GuaranteedHoursOf(string employee)
        => string.Equals(employee, Scenario6SpecConstants.EighteenthEmployee, StringComparison.Ordinal)
            ? Scenario6SpecConstants.EighteenthEmployeeGuaranteedHours
            : AutofillSpecConstants.GuaranteedHours;
}
