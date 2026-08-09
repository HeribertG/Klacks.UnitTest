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
    private const string ExplanationIndent = "      ";

    /// <summary>Pseudo order index of the overall matrix, which draws every order at once.</summary>
    private const int AllOrders = -1;

    private const int SingleOrderCellWidth = 1;

    private const int OverallCellWidth = 2;

    private const string MultiOrderLegend =
        "  In the overall matrix a cell is the order digit followed by the kind letter, for example 1F.\n"
        + "  A per-order matrix uses the one-letter symbols, so it reads like a single-order plan.";

    /// <summary>
    /// Why the multiple-shifts marker is no longer a defect marker. Kept as separate lines so the
    /// legend of the artifact stays readable next to the matrix.
    /// </summary>
    private static readonly string[] MultipleShiftsExplanation =
    [
        "Since the owner correction of 2026-08-08 two different shifts on one day are allowed, and rule 11",
        "asks for them when the day would otherwise leave the employee short of hours. Only the same shift",
        "assigned twice and two shifts overlapping in time are conflicts; those are listed in the metrics",
        "JSON under coverage.doubleBookings, together with which two shifts they are.",
    ];

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

        if (definition.OrdersAreLabelled)
        {
            return RenderMultiOrder(definition, shiftsByEmployee, title, includeCarryIn);
        }

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

    /// <summary>
    /// The matrix of a plan with several parallel orders: one single-character matrix per order, so
    /// each order's rhythm reads exactly like the matrix of a one-order scenario, followed by one
    /// two-character overall matrix whose cells name the order and the kind together (1F, 2S, 3N).
    /// The per-order matrices answer "what does this order look like", the overall one answers "does
    /// anybody hold two orders on one day" — neither picture can be read out of the other.
    /// </summary>
    /// <param name="definition">Scenario that produced the plan</param>
    /// <param name="shiftsByEmployee">Shifts per employee, carry-in days included where applicable</param>
    /// <param name="title">Headline above the matrix</param>
    /// <param name="includeCarryIn">True when the previous month is drawn left of the boundary marker</param>
    private static string RenderMultiOrder(
        AutofillScenarioDefinition definition,
        IReadOnlyDictionary<string, IReadOnlyList<PlannedShift>> shiftsByEmployee,
        string title,
        bool includeCarryIn)
    {
        var firstDay = includeCarryIn ? definition.CarryInFrom!.Value : definition.PeriodFrom;
        var lastDay = definition.PeriodUntil;
        var labelWidth = Math.Max(MinimumLabelWidth, definition.EmployeesInListOrder.Max(e => e.Length));

        var builder = new StringBuilder();
        builder.Append(title).AppendLine();
        builder
            .Append(CultureInfo.InvariantCulture, $"Period {definition.PeriodFrom:yyyy-MM-dd} .. {definition.PeriodUntil:yyyy-MM-dd}")
            .AppendLine();
        builder
            .Append(CultureInfo.InvariantCulture, $"{definition.OrderCount} parallel orders, {AutofillSpecConstants.ShiftsPerDay} shifts each")
            .AppendLine();
        if (includeCarryIn)
        {
            builder
                .Append(CultureInfo.InvariantCulture, $"Carry-in from {firstDay:yyyy-MM-dd} (fixed input, left of the '{MonthBoundaryMarker}')")
                .AppendLine();
        }

        for (var order = AutofillShiftCatalog.FirstOrderIndex;
             order < AutofillShiftCatalog.FirstOrderIndex + definition.OrderCount;
             order++)
        {
            builder.AppendLine();
            builder.Append(CultureInfo.InvariantCulture, $"Order {order}").AppendLine();
            AppendMatrix(builder, definition, shiftsByEmployee, firstDay, lastDay, labelWidth, includeCarryIn, order);
        }

        builder.AppendLine();
        builder.AppendLine("All orders together");
        AppendMatrix(builder, definition, shiftsByEmployee, firstDay, lastDay, labelWidth, includeCarryIn, AllOrders);

        builder.AppendLine();
        builder.Append(BuildLegend());
        builder.AppendLine(MultiOrderLegend);
        return builder.ToString();
    }

    private static void AppendMatrix(
        StringBuilder builder,
        AutofillScenarioDefinition definition,
        IReadOnlyDictionary<string, IReadOnlyList<PlannedShift>> shiftsByEmployee,
        DateOnly firstDay,
        DateOnly lastDay,
        int labelWidth,
        bool includeCarryIn,
        int order)
    {
        var cellWidth = order == AllOrders ? OverallCellWidth : SingleOrderCellWidth;
        builder
            .Append(BuildWideDayHeader(firstDay, lastDay, definition.PeriodFrom, labelWidth, includeCarryIn, cellWidth, tens: true))
            .AppendLine();
        builder
            .Append(BuildWideDayHeader(firstDay, lastDay, definition.PeriodFrom, labelWidth, includeCarryIn, cellWidth, tens: false))
            .AppendLine();

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

                builder.Append(WideCellOf(shifts, day, order, cellWidth));
            }

            builder.AppendLine();
        }
    }

    private static string WideCellOf(IReadOnlyList<PlannedShift> shifts, DateOnly day, int order, int cellWidth)
    {
        var onDay = shifts
            .Where(s => s.Date == day)
            .Where(s => order == AllOrders || s.Order == order)
            .OrderBy(s => s.StartAt)
            .ToList();

        if (onDay.Count == 0)
        {
            return new string(AutofillShiftCatalog.FreeSymbol, 1).PadRight(cellWidth);
        }

        if (onDay.Count > 1)
        {
            return new string(AutofillShiftCatalog.MultipleShiftsSymbol, 1).PadRight(cellWidth);
        }

        return order == AllOrders
            ? AutofillShiftCatalog.OrderSymbolOf(onDay[0].Order, onDay[0].Kind)
            : new string(AutofillShiftCatalog.SymbolOf(onDay[0].Kind), 1).PadRight(cellWidth);
    }

    private static string BuildWideDayHeader(
        DateOnly firstDay,
        DateOnly lastDay,
        DateOnly periodFrom,
        int labelWidth,
        bool includeCarryIn,
        int cellWidth,
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
            var digit = tens && value == 0 ? ' ' : (char)('0' + value);
            builder.Append(digit).Append(new string(' ', cellWidth - 1));
        }

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
            ? AutofillShiftCatalog.MultipleShiftsSymbol
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
            .Append(AutofillShiftCatalog.MultipleShiftsSymbol)
            .AppendLine(" = more than one shift on that day - information, not a defect.");
        foreach (var line in MultipleShiftsExplanation)
        {
            builder.Append(ExplanationIndent).AppendLine(line);
        }
        builder
            .Append("  ")
            .Append(MonthBoundaryMarker)
            .AppendLine(" = start of the planning period; everything left of it is fixed carry-in input");
        builder.AppendLine("  A night shift is shown on the day it starts; it ends at 07:00 of the next day.");
        return builder.ToString();
    }
}
