// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Analysis;

/// <summary>
/// Pins the round-4 rotation oracle (<see cref="RotationSpecAnalyzer"/>) on hand-written plans with the examples of
/// tests/autofill/SPEC-ROTATION-2026-10-08.md, so a red rotation guard later is attributable to the engine.
/// </summary>
[TestFixture]
public sealed class RotationSpecMeasurementTests
{
    private const string Employee = "MA-01";
    private const string Colleague = "MA-02";

    private static readonly DateOnly PeriodFrom = new(2026, 3, 1);
    private static readonly DateOnly PeriodUntil = new(2026, 3, 31);

    [Test]
    public void NightToEarlyAcrossABlockBoundary_IsTheIdealCycleStep()
    {
        var spec = Measure(Plan((1, AutofillShiftKind.Night), (5, AutofillShiftKind.Early)));

        spec.TransitionCount.ShouldBe(1);
        spec.IdealTransitionCount.ShouldBe(1, "N to F is the normal cycle step, never a deviation.");
        spec.UnforcedDeviations.ShouldBe(0);
    }

    [Test]
    public void AscendingBlocks_AreIdeal_AndEachBlockIsPure()
    {
        var spec = Measure(Plan(
            (1, AutofillShiftKind.Early), (2, AutofillShiftKind.Early),
            (6, AutofillShiftKind.Late), (7, AutofillShiftKind.Late),
            (11, AutofillShiftKind.Night)));

        spec.BlockCount.ShouldBe(3);
        spec.BlockPurity.ShouldBe(1.0);
        spec.IdealTransitionRate.ShouldBe(1.0);
    }

    [Test]
    public void AKindChangeInsideABlock_MakesTheBlockImpure()
    {
        var spec = Measure(Plan((1, AutofillShiftKind.Early), (2, AutofillShiftKind.Late)));

        spec.BlockCount.ShouldBe(1, "One day apart is far below 48 h of rest.");
        spec.PureBlockCount.ShouldBe(0);
        spec.BlockPurity.ShouldBe(0.0);
    }

    [Test]
    public void TheSameKindInTheNextBlock_IsAnUnforcedDeviation_WhenASwapWithTheLateHolderWasFeasible()
    {
        var spec = Measure(
            Plan((1, AutofillShiftKind.Early), (5, AutofillShiftKind.Early)),
            colleagueShifts: [(Colleague, 5, AutofillShiftKind.Late)]);

        spec.IdealTransitionCount.ShouldBe(0);
        spec.UnforcedDeviations.ShouldBe(1);
        var deviation = spec.Deviations.Single();
        deviation.Ideal.ShouldBe(AutofillShiftKind.Late);
        deviation.Cause.ShouldBe(RotationSpecAnalyzer.CauseUnforced);
    }

    /// <summary>
    /// In a fully staffed plan every late slot is held by someone else, so "staffed by others" alone would excuse every
    /// deviation. Only a swap the hard rules forbid does: here the late holder may not work early that day.
    /// </summary>
    [Test]
    public void TheSameKindInTheNextBlock_IsForcedByCoverage_WhenTheLateHolderMayNotTakeTheEarlyShift()
    {
        var commands = new AutofillScheduleCommandInput(
            [new AutofillScheduleCommand(Colleague, ScheduleCommandKeyword.NoEarly, new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 5))]);

        var spec = Measure(
            Plan((1, AutofillShiftKind.Early), (5, AutofillShiftKind.Early)),
            commands,
            colleagueShifts: [(Colleague, 5, AutofillShiftKind.Late)]);

        spec.UnforcedDeviations.ShouldBe(0);
        spec.Deviations.Single().Cause.ShouldBe(RotationSpecAnalyzer.CauseCoverage);
    }

    [Test]
    public void TheSameKindInTheNextBlock_IsForcedByCoverage_WhenNobodyHoldsALateShiftThatDay()
    {
        var spec = Measure(Plan((1, AutofillShiftKind.Early), (5, AutofillShiftKind.Early)));

        spec.UnforcedDeviations.ShouldBe(0);
        spec.Deviations.Single().Cause.ShouldBe(RotationSpecAnalyzer.CauseCoverage);
    }

    [Test]
    public void AfterALongPause_TheRotationRestartsAtEarly()
    {
        var restart = Measure(Plan((1, AutofillShiftKind.Night), (16, AutofillShiftKind.Early)));
        var continued = Measure(Plan((1, AutofillShiftKind.Early), (16, AutofillShiftKind.Late)));

        restart.LongPauseRestartCount.ShouldBe(1);
        restart.IdealTransitionCount.ShouldBe(1, "After 14 free days the cycle restarts at early (S6-5 is superseded).");
        continued.IdealTransitionCount.ShouldBe(0, "After a long pause late is not the restart kind, even after early.");
    }

    [Test]
    public void ADisallowedKind_IsSkipped()
    {
        var commands = new AutofillScheduleCommandInput(
            [new AutofillScheduleCommand(Employee, ScheduleCommandKeyword.NoNight, PeriodFrom, PeriodUntil)]);

        var spec = Measure(Plan((1, AutofillShiftKind.Late), (5, AutofillShiftKind.Early)), commands);

        spec.IdealTransitionCount.ShouldBe(1, "With night closed the cycle is F, S, F, S.");
    }

    [Test]
    public void OnlyOneAllowedKind_OwesNoRotation()
    {
        var commands = new AutofillScheduleCommandInput(
            [new AutofillScheduleCommand(Employee, ScheduleCommandKeyword.OnlyEarly, PeriodFrom, PeriodUntil)]);

        var spec = Measure(Plan((1, AutofillShiftKind.Early), (5, AutofillShiftKind.Early)), commands);

        spec.TransitionCount.ShouldBe(0);
        spec.SingleKindSkipCount.ShouldBe(1);
    }

    [Test]
    public void TheIdealKindClosedOnTheFirstDay_ForcesTheDeviation()
    {
        var commands = new AutofillScheduleCommandInput(
            [new AutofillScheduleCommand(Employee, ScheduleCommandKeyword.NoLate, new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 5))]);

        var spec = Measure(Plan((1, AutofillShiftKind.Early), (5, AutofillShiftKind.Night), (6, AutofillShiftKind.Night)), commands);

        spec.UnforcedDeviations.ShouldBe(0);
        spec.ForcedDeviationCount.ShouldBe(1);
        spec.Deviations.Single().Cause.ShouldBe(RotationSpecAnalyzer.CauseHardRule);
    }

    private static (int Day, AutofillShiftKind Kind)[] Plan(params (int Day, AutofillShiftKind Kind)[] shifts) => shifts;

    private static RotationSpecMetrics Measure(
        (int Day, AutofillShiftKind Kind)[] plan,
        AutofillScheduleCommandInput? commands = null,
        (string Employee, int Day, AutofillShiftKind Kind)[]? colleagueShifts = null)
    {
        var builder = new AutofillScenarioBuilder()
            .WithPeriod(PeriodFrom, PeriodUntil)
            .AddEmployee(Employee, AutofillSpecConstants.GuaranteedHours);
        foreach (var colleague in (colleagueShifts ?? []).Select(c => c.Employee).Distinct())
        {
            builder.AddEmployee(colleague, AutofillSpecConstants.GuaranteedHours);
        }

        if (commands != null)
        {
            builder.WithScheduleCommands(commands);
        }

        var definition = builder.Build();
        var tokens = plan.Select(p => Token(Employee, PeriodFrom.AddDays(p.Day - 1), p.Kind))
            .Concat((colleagueShifts ?? []).Select(c => Token(c.Employee, PeriodFrom.AddDays(c.Day - 1), c.Kind)))
            .ToList();
        return AutofillPlanAnalyzer.Analyze(new CoreScenario { Tokens = tokens }, definition, "test", "test", "run1")
            .Rotation.Spec;
    }

    private static CoreToken Token(string employee, DateOnly date, AutofillShiftKind kind)
    {
        var (startAt, endAt) = AutofillShiftCatalog.SpanOf(kind, date);
        return new CoreToken(
            WorkIds: [$"{employee}-{date:yyyyMMdd}-{kind}"],
            ShiftTypeIndex: AutofillShiftCatalog.ShiftTypeIndexOf(kind),
            Date: date,
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
