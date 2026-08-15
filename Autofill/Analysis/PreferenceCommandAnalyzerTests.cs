// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Analysis;

/// <summary>
/// Pure-computation checks of the day-shift, preference and schedule-command measures, plus the
/// attributed control-group diff. No engine run is involved: these tests pin the MEASURING INSTRUMENT,
/// so that a red scenario-5 assertion later is attributable to the algorithm and not to the analyzer.
/// <para>
/// The plans below are written by hand, a handful of tokens each, and every one of them is built to
/// make one specific mistake detectable. The case that matters most is the pair of a day shift and a
/// late shift on the SAME day and the SAME order: both carry the engine's late class, so any measure
/// that reasons in classes rather than in shift reference ids will confuse them, and every metric here
/// exists precisely because the engine cannot tell them apart.
/// </para>
/// </summary>
[TestFixture]
public sealed class PreferenceCommandAnalyzerTests
{
    private const string DayLover = "MA-16";
    private const string Ordinary = "MA-01";
    private const int OrderOne = 1;
    private const int OrderTwo = 2;

    private static readonly DateOnly Day1 = new(2026, 3, 1);
    private static readonly DateOnly Day2 = new(2026, 3, 2);
    private static readonly DateOnly Day3 = new(2026, 3, 3);

    [Test]
    public void ADayShiftIsCountedAsADayShiftAndALateShiftIsNot()
    {
        var definition = BuildDefinition(withPreferences: true, withCommands: false);
        var plan = PlanOf(
            Token(DayLover, Day1, AutofillShiftKind.Day, OrderOne),
            Token(Ordinary, Day1, AutofillShiftKind.Late, OrderOne),
            Token(Ordinary, Day2, AutofillShiftKind.Day, OrderTwo));

        var metrics = AutofillPlanAnalyzer.Analyze(plan, definition, "test", "test", "run1");

        metrics.DayShift.Assignments.Count.ShouldBe(
            2,
            "Only the two day shifts may be counted. The late shift of the same day and the same order carries the "
            + "identical engine class, so a class-based count would report three.");

        metrics.DayShift.Assignments.ShouldAllBe(
            a => AutofillShiftCatalog.IsDayShift(a.ShiftRefId),
            "Every day-shift entry has to resolve back to a day shift through its shift reference id.");

        metrics.DayShift.Assignments
            .Single(a => a.Employee == DayLover)
            .ViaPreference.ShouldBeTrue("MA-16 prefers day shifts, so his day shift is flagged as preferred.");

        metrics.DayShift.Assignments
            .Single(a => a.Employee == Ordinary)
            .ViaPreference.ShouldBeFalse(
                "A day shift held by somebody who never asked for one is NOT a preference violation and must not be "
                + "flagged as satisfying a preference either.");

        var lover = metrics.DayShift.ShareByEmployee.Single(s => s.Employee == DayLover);
        lover.DayShiftCount.ShouldBe(1);
        lover.TotalCount.ShouldBe(1);
        lover.Share.ShouldBe(1.0);
        lover.PrefersDayShifts.ShouldBeTrue();

        var plain = metrics.DayShift.ShareByEmployee.Single(s => s.Employee == Ordinary);
        plain.DayShiftCount.ShouldBe(1);
        plain.TotalCount.ShouldBe(2);
        plain.PrefersDayShifts.ShouldBeFalse();
    }

    [Test]
    public void ANightShiftOfABlacklistedEmployeeIsReported()
    {
        var definition = BuildDefinition(withPreferences: true, withCommands: false);
        var plan = PlanOf(
            Token(DayLover, Day1, AutofillShiftKind.Night, OrderOne),
            Token(Ordinary, Day1, AutofillShiftKind.Night, OrderTwo));

        var metrics = AutofillPlanAnalyzer.Analyze(plan, definition, "test", "test", "run1");

        metrics.Preferences.BlacklistViolations.Count.ShouldBe(
            1,
            "Only the blacklisted employee's night shift is a violation; the other employee may work nights. This is "
            + "the scan that has to exist because the engine has no ViolationKind for a blacklisted assignment and "
            + "therefore never reports one itself.");

        var violation = metrics.Preferences.BlacklistViolations[0];
        violation.Employee.ShouldBe(DayLover);
        violation.SlotKind.ShouldBe(AutofillShiftKind.Night);
        violation.Order.ShouldBe(OrderOne);
        violation.IsCarryIn.ShouldBeFalse();
    }

    [Test]
    public void APlanWithoutPreferencesReportsNoPreferenceMetricsAtAll()
    {
        var definition = BuildDefinition(withPreferences: false, withCommands: false);
        var plan = PlanOf(Token(DayLover, Day1, AutofillShiftKind.Night, OrderOne));

        var metrics = AutofillPlanAnalyzer.Analyze(plan, definition, "test", "test", "run1");

        metrics.Preferences.ShouldBe(
            PreferenceMetrics.Empty,
            "A scenario that declares no preference must measure exactly what it measured before the preference "
            + "family existed — that is what keeps scenarios 1 to 4 unchanged.");
    }

    [Test]
    public void AShiftInsideAFreeWindowIsAViolationAndTheWindowIsAlwaysReported()
    {
        var definition = BuildDefinition(withPreferences: false, withCommands: true);
        var plan = PlanOf(
            Token(Ordinary, Day2, AutofillShiftKind.Early, OrderOne),
            Token(DayLover, Day2, AutofillShiftKind.Late, OrderOne));

        var metrics = AutofillPlanAnalyzer.Analyze(plan, definition, "test", "test", "run1");

        metrics.Keyword.ScheduleCommandViolations.Count.ShouldBe(
            1,
            "MA-01 carries FREE on 2 March and stands on an early shift that day; MA-16 carries no command and is "
            + "free to work.");

        var violation = metrics.Keyword.ScheduleCommandViolations[0];
        violation.Employee.ShouldBe(Ordinary);
        violation.Keyword.ShouldBe(ScheduleCommandKeyword.Free);
        violation.Date.ShouldBe(Day2);

        metrics.Keyword.Windows.Count.ShouldBe(
            1,
            "The window has to be reported whether or not it was broken: an HONOURED free window produces no "
            + "violation and no assignment, so without the window list it would be indistinguishable from a window "
            + "that was never applied at all.");
        metrics.Keyword.Windows[0].AssignmentsInWindow.Count.ShouldBe(1);
        metrics.Keyword.Windows[0].AssignmentsInWindow[0].ViolatesKeyword.ShouldBeTrue();
    }

    [Test]
    public void TheDayShiftIsJudgedAsALateShiftByEveryKeyword()
    {
        var onlyLate = new AutofillScheduleCommand(Ordinary, ScheduleCommandKeyword.OnlyLate, Day1, Day1);
        var onlyEarly = new AutofillScheduleCommand(Ordinary, ScheduleCommandKeyword.OnlyEarly, Day1, Day1);

        onlyLate.ForbidsKind(AutofillShiftKind.Day).ShouldBeFalse(
            "OnlyLate permits the day shift, because the engine classifies 08:00-16:00 as late. A keyword check that "
            + "reasoned about slot kinds instead of classes would forbid it and contradict the engine.");
        onlyLate.ForbidsKind(AutofillShiftKind.Late).ShouldBeFalse();
        onlyLate.ForbidsKind(AutofillShiftKind.Early).ShouldBeTrue();

        onlyEarly.ForbidsKind(AutofillShiftKind.Day).ShouldBeTrue(
            "OnlyEarly forbids the day shift for the same reason: it is a late shift to the engine.");
    }

    [Test]
    public void NotFreeIsANoOpExactlyAsItIsInTheEngine()
    {
        var notFree = new AutofillScheduleCommand(Ordinary, ScheduleCommandKeyword.NotFree, Day1, Day1);

        foreach (var kind in AutofillShiftCatalog.SlotKindsOf(includeDayShift: true))
        {
            notFree.ForbidsKind(kind).ShouldBeFalse(
                "NotFree is declared in the engine's enum but handled by no switch in the engine — "
                + "Stage0HardConstraintChecker, SlotConstraintFilter, PlanConstraintChecker and MaxPossibleCalculator "
                + "all fall through to their default arm. The fixture must mirror that instead of inventing a "
                + "meaning, and the builder rejects the keyword outright.");
        }
    }

    /// <summary>
    /// The case the attributed diff exists for, and the one a class-keyed comparison gets wrong: two
    /// slots of the SAME day and the SAME late class — a day shift and a late shift — change hands in
    /// opposite directions. Only the shift reference id tells them apart, so only a diff that carries
    /// it can attribute each of them to the right cause.
    /// </summary>
    [Test]
    public void TheDiffKeepsADayShiftAndALateShiftOfOneDayApart()
    {
        var treatmentDefinition = BuildDefinition(withPreferences: true, withCommands: false);
        var controlDefinition = BuildDefinition(withPreferences: false, withCommands: false);

        var treatment = PlanOf(
            Token(DayLover, Day1, AutofillShiftKind.Day, OrderOne),
            Token(Ordinary, Day1, AutofillShiftKind.Late, OrderOne));
        var control = PlanOf(
            Token(Ordinary, Day1, AutofillShiftKind.Day, OrderOne),
            Token(DayLover, Day1, AutofillShiftKind.Late, OrderOne));

        var diff = PreferenceCommandAnalyzer.AttributeDiff(
            treatment, treatmentDefinition, control, controlDefinition);

        diff.ChangedAssignments.Count.ShouldBe(
            2, "Both slots changed hands and both must appear, each with its own shift reference.");

        var dayChange = diff.ChangedAssignments.Single(c => c.SlotKind == AutofillShiftKind.Day);
        dayChange.EmployeeTreatment.ShouldBe(DayLover);
        dayChange.EmployeeControl.ShouldBe(Ordinary);
        dayChange.Order.ShouldBe(OrderOne);
        dayChange.ShiftClass.ShouldBe(
            AutofillShiftKind.Late,
            "The day shift's engine class stays late even where the slot kind separates it from the late shift.");
        dayChange.AttributedTo.ShouldBe(
            DiffAttribution.PreferenceGained,
            "MA-16 took a shift he holds a Preferred entry for, which is the direct cause.");

        var lateChange = diff.ChangedAssignments.Single(c => c.SlotKind == AutofillShiftKind.Late);
        lateChange.EmployeeTreatment.ShouldBe(Ordinary);
        lateChange.EmployeeControl.ShouldBe(DayLover);
        lateChange.AttributedTo.ShouldNotBe(
            DiffAttribution.Unexplained,
            "MA-16 is constrained in this scenario, so the slot he gave up is at worst a knock-on and never "
            + "unexplained.");

        diff.UnexplainedCount.ShouldBe(0);
        diff.AttributionRule.ShouldBe(PreferenceCommandAnalyzer.DiffAttributionRule);
    }

    /// <summary>
    /// The counter-case: a change that no preference and no command can explain must be counted as
    /// unexplained. Without this the attribution rule could quietly become a rule that explains
    /// everything, and the specification's unexplainedCount would stop being a measurement.
    /// </summary>
    [Test]
    public void AChangeAmongUnconstrainedEmployeesStaysUnexplained()
    {
        var treatmentDefinition = BuildDefinition(withPreferences: true, withCommands: false);
        var controlDefinition = BuildDefinition(withPreferences: false, withCommands: false);

        var treatment = PlanOf(Token(Ordinary, Day3, AutofillShiftKind.Early, OrderOne));
        var control = PlanOf(Token(Bystander, Day3, AutofillShiftKind.Early, OrderOne));

        var diff = PreferenceCommandAnalyzer.AttributeDiff(
            treatment, treatmentDefinition, control, controlDefinition);

        diff.ChangedAssignments.Count.ShouldBe(1);
        diff.ChangedAssignments[0].AttributedTo.ShouldBe(
            DiffAttribution.Unexplained,
            "Neither employee carries a preference or a command anywhere, so nothing in the two differing inputs can "
            + "account for the change. This must be counted, not explained away.");
        diff.UnexplainedCount.ShouldBe(1);
    }

    private const string Bystander = "MA-02";

    private static AutofillScenarioDefinition BuildDefinition(bool withPreferences, bool withCommands)
    {
        var builder = new AutofillScenarioBuilder()
            .WithPeriod(Day1, Day3)
            .WithOrders(Scenario5OrderCount)
            .WithDayShiftPerOrder()
            .AddEmployee(Ordinary, AutofillSpecConstants.GuaranteedHours)
            .AddEmployee(Bystander, AutofillSpecConstants.GuaranteedHours)
            .AddEmployee(DayLover, AutofillSpecConstants.GuaranteedHours);

        if (withPreferences)
        {
            builder.WithShiftPreferences(new AutofillShiftPreferenceInput(
            [
                .. AutofillShiftPreference.ForEveryOrder(
                    DayLover, AutofillShiftKind.Day, ShiftPreferenceKind.Preferred, Scenario5OrderCount),
                .. AutofillShiftPreference.ForEveryOrder(
                    DayLover, AutofillShiftKind.Night, ShiftPreferenceKind.Blacklist, Scenario5OrderCount),
            ]));
        }

        if (withCommands)
        {
            builder.WithScheduleCommands(new AutofillScheduleCommandInput(
            [
                new AutofillScheduleCommand(Ordinary, ScheduleCommandKeyword.Free, Day2, Day2),
            ]));
        }

        return builder.Build();
    }

    private const int Scenario5OrderCount = 2;

    private static CoreScenario PlanOf(params CoreToken[] tokens)
        => new() { Tokens = [.. tokens] };

    private static CoreToken Token(string agentId, DateOnly date, AutofillShiftKind kind, int order)
    {
        var (startAt, endAt) = AutofillShiftCatalog.SpanOf(kind, date);
        return new CoreToken(
            WorkIds: [$"{agentId}-{date:yyyyMMdd}-{kind}"],
            ShiftTypeIndex: AutofillShiftCatalog.ShiftTypeIndexOf(kind),
            Date: date,
            TotalHours: (decimal)AutofillSpecConstants.ShiftHours,
            StartAt: startAt,
            EndAt: endAt,
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: AutofillShiftCatalog.LocationContextOf(order),
            ShiftRefId: AutofillShiftCatalog.ShiftIdOf(order, kind),
            AgentId: agentId);
    }
}
