// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using System.Text;
using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis;

/// <summary>
/// Draws a plan as an ASCII matrix: one row per employee in list order, one column per calendar day,
/// one letter per cell. When the scenario has a carry-in month, the fixed previous-month days are
/// drawn to the left of a month-boundary marker so the continuation across the boundary is visible
/// at a glance.
/// </summary>
public static class PlanMatrixRenderer
{
    private const char MonthBoundaryMarker = '|';
    private const string RowLabelSeparator = " | ";
    private const int MinimumLabelWidth = 6;

    /// <summary>
    /// Renders the matrix.
    /// </summary>
    /// <param name="scenario">Plan produced by the engine</param>
    /// <param name="definition">Scenario that produced it</param>
    /// <param name="title">Headline above the matrix, normally scenario and run label</param>
    public static string Render(CoreScenario scenario, AutofillScenarioDefinition definition, string title)
    {
        var includeCarryIn = definition.CarryInFrom.HasValue;
        var shiftsByEmployee = AutofillPlanAnalyzer.BuildPlannedShifts(scenario, definition, includeCarryIn);

        var firstDay = includeCarryIn ? definition.CarryInFrom!.Value : definition.PeriodFrom;
        var lastDay = definition.PeriodUntil;
        var labelWidth = Math.Max(MinimumLabelWidth, definition.EmployeesInListOrder.Max(e => e.Length));

        var builder = new StringBuilder();
        builder.Append(title).AppendLine();
        builder
            .Append(CultureInfo.InvariantCulture, $"Period {definition.PeriodFrom:yyyy-MM-dd} .. {definition.PeriodUntil:yyyy-MM-dd}")
            .AppendLine();
        if (includeCarryIn)
        {
            builder
                .Append(CultureInfo.InvariantCulture, $"Carry-in from {firstDay:yyyy-MM-dd} (fixed input, left of the '{MonthBoundaryMarker}')")
                .AppendLine();
        }

        builder.AppendLine();
        builder.Append(BuildDayHeader(firstDay, lastDay, definition.PeriodFrom, labelWidth, includeCarryIn, tens: true)).AppendLine();
        builder.Append(BuildDayHeader(firstDay, lastDay, definition.PeriodFrom, labelWidth, includeCarryIn, tens: false)).AppendLine();

        foreach (var employee in definition.EmployeesInListOrder)
        {
            builder.Append(employee.PadRight(labelWidth)).Append(RowLabelSeparator);
            var shifts = shiftsByEmployee[employee];
            for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
            {
                if (includeCarryIn && day == definition.PeriodFrom)
                {
                    builder.Append(MonthBoundaryMarker);
                }

                builder.Append(CellOf(shifts, day));
            }

            builder.AppendLine();
        }

        builder.AppendLine();
        builder.Append(BuildLegend());
        return builder.ToString();
    }

    private static char CellOf(IReadOnlyList<PlannedShift> shifts, DateOnly day)
    {
        var onDay = shifts.Where(s => s.Date == day).ToList();
        if (onDay.Count == 0)
        {
            return AutofillShiftCatalog.FreeSymbol;
        }

        return onDay.Count > 1
            ? AutofillShiftCatalog.DoubleBookedSymbol
            : AutofillShiftCatalog.SymbolOf(onDay[0].Kind);
    }

    private static string BuildDayHeader(
        DateOnly firstDay,
        DateOnly lastDay,
        DateOnly periodFrom,
        int labelWidth,
        bool includeCarryIn,
        bool tens)
    {
        var builder = new StringBuilder();
        builder.Append(new string(' ', labelWidth)).Append(RowLabelSeparator);
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            if (includeCarryIn && day == periodFrom)
            {
                builder.Append(MonthBoundaryMarker);
            }

            var value = tens ? day.Day / 10 : day.Day % 10;
            builder.Append(tens && value == 0 ? ' ' : (char)('0' + value));
        }

        return builder.ToString();
    }

    private static string BuildLegend()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Legend:");
        foreach (var kind in AutofillShiftCatalog.Kinds)
        {
            builder
                .Append("  ")
                .Append(AutofillShiftCatalog.SymbolOf(kind))
                .Append(" = ")
                .Append(kind.ToString().ToLowerInvariant())
                .Append(' ')
                .Append(AutofillShiftCatalog.StartTimeOf(kind))
                .Append('-')
                .Append(AutofillShiftCatalog.EndTimeOf(kind))
                .AppendLine();
        }

        builder
            .Append("  ")
            .Append(AutofillShiftCatalog.FreeSymbol)
            .AppendLine(" = free day");
        builder
            .Append("  ")
            .Append(AutofillShiftCatalog.DoubleBookedSymbol)
            .AppendLine(" = more than one shift on that day; the detail is in the metrics JSON");
        builder
            .Append("  ")
            .Append(MonthBoundaryMarker)
            .AppendLine(" = start of the planning period; everything left of it is fixed carry-in input");
        builder.AppendLine("  A night shift is shown on the day it starts; it ends at 07:00 of the next day.");
        return builder.ToString();
    }
}
