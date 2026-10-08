// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Reflection;
using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.Autofill.Fixtures;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Analysis;

/// <summary>
/// Pins the coverage measurement per seat (SPEC-SZENARIO-M A1). Production expands a shift with several
/// seats into several CoreShift rows with the same id and date, and the engine sums them into one slot of
/// that capacity. The analyzer has to measure the same way: one assignment on a two-seat slot fills one
/// seat and leaves one open, it must not read as two filled rows.
/// </summary>
[TestFixture]
public sealed class CoverageMeasurementTests
{
    private const string Employee = "MA-01";
    private const string SecondEmployee = "MA-02";

    private static readonly DateOnly Day1 = new(2026, 3, 2);

    [Test]
    public void OneAssignmentOnATwoSeatSlotFillsOneSeatAndLeavesOneOpen()
    {
        var definition = DefinitionWithSecondEarlySeat();

        var metrics = Analyze(definition, Token(Employee, AutofillShiftKind.Early));

        metrics.Coverage.TotalRequiredShifts.ShouldBe(4, "Early has two seats, late and night one each.");
        metrics.Coverage.FilledShifts.ShouldBe(1, "Only one seat of the two-seat early slot is staffed.");
        metrics.Coverage.UnfilledShifts.Count(u => u.ShiftType == AutofillShiftKind.Early).ShouldBe(
            1, "The second early seat stays open and must be reported as one open seat.");
        metrics.Coverage.UnfilledShifts.Count.ShouldBe(3);
        metrics.Coverage.OversuppliedSlots.ShouldBe(0);
    }

    [Test]
    public void TwoAssignmentsOnATwoSeatSlotFillItWithoutOversupply()
    {
        var definition = DefinitionWithSecondEarlySeat();

        var metrics = Analyze(
            definition,
            Token(Employee, AutofillShiftKind.Early),
            Token(SecondEmployee, AutofillShiftKind.Early));

        metrics.Coverage.FilledShifts.ShouldBe(2);
        metrics.Coverage.UnfilledShifts.Count(u => u.ShiftType == AutofillShiftKind.Early).ShouldBe(0);
        metrics.Coverage.OversuppliedSlots.ShouldBe(0, "Two assignments on two seats are exact demand.");
    }

    [Test]
    public void TwoAssignmentsOnASingleSeatSlotAreOneOversuppliedSlot()
    {
        var definition = BaseDefinition();

        var metrics = Analyze(
            definition,
            Token(Employee, AutofillShiftKind.Early),
            Token(SecondEmployee, AutofillShiftKind.Early));

        metrics.Coverage.TotalRequiredShifts.ShouldBe(3);
        metrics.Coverage.FilledShifts.ShouldBe(1);
        metrics.Coverage.OversuppliedSlots.ShouldBe(1);
    }

    private static AutofillScenarioDefinition BaseDefinition() =>
        new AutofillScenarioBuilder()
            .WithPeriod(Day1, Day1)
            .AddEmployee(Employee, AutofillSpecConstants.GuaranteedHours)
            .AddEmployee(SecondEmployee, AutofillSpecConstants.GuaranteedHours)
            .Build();

    private static AutofillScenarioDefinition DefinitionWithSecondEarlySeat()
    {
        var definition = BaseDefinition();
        var earlyId = AutofillShiftCatalog.ShiftIdOf(AutofillShiftCatalog.SingleOrderIndex, AutofillShiftKind.Early).ToString();
        var earlyRow = definition.Context.Shifts.Single(s => s.Id == earlyId);
        var shifts = definition.Context.Shifts.Append(earlyRow).ToList();
        return definition with { Context = CopyWithShifts(definition.Context, shifts) };
    }

    private static CoreWizardContext CopyWithShifts(CoreWizardContext source, IReadOnlyList<CoreShift> shifts)
    {
        var copy = new CoreWizardContext();
        foreach (var property in typeof(CoreWizardContext).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanWrite)
            {
                property.SetValue(copy, property.GetValue(source));
            }
        }

        typeof(CoreWizardContext).GetProperty(nameof(CoreWizardContext.Shifts))!.SetValue(copy, shifts);
        return copy;
    }

    private static Klacks.UnitTest.Autofill.Analysis.Model.AutofillMetrics Analyze(
        AutofillScenarioDefinition definition, params CoreToken[] tokens) =>
        AutofillPlanAnalyzer.Analyze(new CoreScenario { Tokens = [.. tokens] }, definition, "test", "test", "run1");

    private static CoreToken Token(string employee, AutofillShiftKind kind)
    {
        var (startAt, endAt) = AutofillShiftCatalog.SpanOf(kind, Day1);
        return new CoreToken(
            WorkIds: [$"{employee}-{Day1:yyyyMMdd}-{kind}"],
            ShiftTypeIndex: AutofillShiftCatalog.ShiftTypeIndexOf(kind),
            Date: Day1,
            TotalHours: (decimal)AutofillSpecConstants.ShiftHours,
            StartAt: startAt,
            EndAt: endAt,
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: AutofillShiftCatalog.LocationContextOf(AutofillShiftCatalog.SingleOrderIndex),
            ShiftRefId: AutofillShiftCatalog.ShiftIdOf(AutofillShiftCatalog.SingleOrderIndex, kind),
            AgentId: employee);
    }
}
