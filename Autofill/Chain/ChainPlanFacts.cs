// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.Autofill.Analysis;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Chain;

/// <summary>
/// Plan facts the chain measurement computes itself instead of asking an engine checker. Weekly hours follow the rule of
/// every wizard (CalendarWeekHours): ISO week Monday to Sunday, a shift counts with its paid hours in the week of its
/// start day, the previous-month works included so the seam week is complete. Daily hours count in-period shifts by
/// start day. A package is the analyzer's package (a run of consecutive calendar days with at least one shift), so two
/// packages meet where the next shift lies more than one calendar day after the previous one; the rest between them is
/// measured from shift end to shift start, the seam to the previous month included.
/// </summary>
public static class ChainPlanFacts
{
    private const int ConsecutiveDayStep = 1;

    /// <summary>Highest paid hours of one ISO week per employee, in list order.</summary>
    /// <param name="plan">Plan of one stage</param>
    /// <param name="definition">Scenario that produced it</param>
    public static IReadOnlyList<(string Employee, double Hours)> HighestWeeklyHours(
        CoreScenario plan, AutofillScenarioDefinition definition)
    {
        var shifts = AutofillPlanAnalyzer.BuildPlannedShifts(plan, definition, includeCarryIn: true);
        return definition.EmployeesInListOrder
            .Select(employee => (Employee: employee, Hours: shifts[employee]
                .GroupBy(s => WeekOf(s.Date))
                .Select(week => week.Sum(s => s.Hours))
                .DefaultIfEmpty()
                .Max()))
            .ToList();
    }

    /// <summary>Every ISO week of an employee above the agent's weekly cap; a cap of 0 or less is no cap.</summary>
    /// <param name="plan">Plan of one stage</param>
    /// <param name="definition">Scenario that produced it</param>
    public static IReadOnlyList<string> WeeklyCapViolations(CoreScenario plan, AutofillScenarioDefinition definition)
    {
        var shifts = AutofillPlanAnalyzer.BuildPlannedShifts(plan, definition, includeCarryIn: true);
        var violations = new List<string>();
        foreach (var agent in definition.Context.Agents.Where(a => a.MaxWeeklyHours > 0))
        {
            violations.AddRange(shifts[agent.Id]
                .GroupBy(s => WeekOf(s.Date))
                .Select(week => (week.Key, Hours: week.Sum(s => s.Hours)))
                .Where(week => week.Hours > agent.MaxWeeklyHours)
                .Select(week => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{agent.Id} ISO week {week.Key.Year}-{week.Key.Week}: {week.Hours} h, cap {agent.MaxWeeklyHours} h")));
        }

        return violations;
    }

    /// <summary>Every day of an employee above the agent's daily cap; a cap of 0 or less is no cap.</summary>
    /// <param name="plan">Plan of one stage</param>
    /// <param name="definition">Scenario that produced it</param>
    public static IReadOnlyList<string> DailyCapViolations(CoreScenario plan, AutofillScenarioDefinition definition)
    {
        var shifts = AutofillPlanAnalyzer.BuildPlannedShifts(plan, definition, includeCarryIn: false);
        var violations = new List<string>();
        foreach (var agent in definition.Context.Agents.Where(a => a.MaxDailyHours > 0))
        {
            violations.AddRange(shifts[agent.Id]
                .GroupBy(s => s.Date)
                .Select(day => (day.Key, Hours: day.Sum(s => s.Hours)))
                .Where(day => day.Hours > agent.MaxDailyHours)
                .Select(day => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{agent.Id} {day.Key:yyyy-MM-dd}: {day.Hours} h, cap {agent.MaxDailyHours} h")));
        }

        return violations;
    }

    /// <summary>
    /// Pairs of consecutive packages of one employee with less rest between them than the block boundary of
    /// SPEC-ROTATION (<see cref="RotationSpecAnalyzer.BlockBoundaryRestHours"/>); a pair counts when its second package
    /// starts in the period.
    /// </summary>
    /// <param name="plan">Plan of one stage</param>
    /// <param name="definition">Scenario that produced it</param>
    public static int ShortRestPackagePairs(CoreScenario plan, AutofillScenarioDefinition definition)
    {
        var shifts = AutofillPlanAnalyzer.BuildPlannedShifts(plan, definition, includeCarryIn: true);
        var pairs = 0;
        foreach (var employee in definition.EmployeesInListOrder)
        {
            var sequence = shifts[employee];
            for (var i = 1; i < sequence.Count; i++)
            {
                var previous = sequence[i - 1];
                var next = sequence[i];
                var startsNewPackage = next.Date.DayNumber - previous.Date.DayNumber > ConsecutiveDayStep;
                if (startsNewPackage
                    && next.Date >= definition.PeriodFrom
                    && (next.StartAt - previous.EndAt).TotalHours < RotationSpecAnalyzer.BlockBoundaryRestHours)
                {
                    pairs++;
                }
            }
        }

        return pairs;
    }

    /// <summary>In-period Saturdays and Sundays with at least one shift, per employee in list order.</summary>
    /// <param name="plan">Plan of one stage</param>
    /// <param name="definition">Scenario that produced it</param>
    public static IReadOnlyList<(string Employee, int Days)> WeekendDays(CoreScenario plan, AutofillScenarioDefinition definition)
    {
        var shifts = AutofillPlanAnalyzer.BuildPlannedShifts(plan, definition, includeCarryIn: false);
        return definition.EmployeesInListOrder
            .Select(employee => (Employee: employee, Days: shifts[employee]
                .Where(s => s.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                .Select(s => s.Date)
                .Distinct()
                .Count()))
            .ToList();
    }

    private static (int Year, int Week) WeekOf(DateOnly date)
    {
        var day = date.ToDateTime(TimeOnly.MinValue);
        return (ISOWeek.GetYear(day), ISOWeek.GetWeekOfYear(day));
    }
}
