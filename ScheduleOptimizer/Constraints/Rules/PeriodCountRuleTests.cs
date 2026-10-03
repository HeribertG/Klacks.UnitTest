// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints.Rules;
using static Klacks.UnitTest.ScheduleOptimizer.Constraints.Rules.RuleTestFactory;

namespace Klacks.UnitTest.ScheduleOptimizer.Constraints.Rules;

/// <summary>
/// PeriodCount rule: calendar anchoring (ISO week, month, year), carry-in from boundary segments,
/// cross-midnight night and overlong segments, break days as free days and the slot-incremental check.
/// </summary>
[TestFixture]
public class PeriodCountRuleTests
{
    [Test]
    public void Week_SixWorkedDaysInIsoWeek_ReportsAtFirstPlanDayOfWeek()
    {
        var context = Context(Monday, Monday.AddDays(6));
        var plan = Plan(context, [.. Days(Monday, 6).Select(d => Early(AgentA, d))]);

        var finding = Evaluator(context, Rule(RuleCounterEvent.WorkedDayInWeek, RuleCalendarPeriod.Week, 6)).Evaluate(plan)
            .Findings.ShouldHaveSingleItem();

        finding.Date.ShouldBe(Monday);
        finding.Observed.ShouldBe(6m);
        finding.Limit.ShouldBe(6m);
    }

    [Test]
    public void Week_FiveWorkedDaysAndOneBreak_NoFinding()
    {
        var context = Context(Monday, Monday.AddDays(6));
        var plan = Plan(context, [.. Days(Monday, 5).Select(d => Early(AgentA, d))]);

        Evaluator(context, Rule(RuleCounterEvent.WorkedDayInWeek, RuleCalendarPeriod.Week, 6)).Evaluate(plan).Findings.ShouldBeEmpty();
    }

    [Test]
    public void Week_TwoSegmentsOnOneDay_CountAsOneWorkedDay()
    {
        var context = Context(Monday, Monday.AddDays(6));
        var plan = Plan(context, [.. Days(Monday, 5).Select(d => Early(AgentA, d)), Late(AgentA, Monday)]);

        Evaluator(context, Rule(RuleCounterEvent.WorkedDayInWeek, RuleCalendarPeriod.Week, 6)).Evaluate(plan).Findings.ShouldBeEmpty();
    }

    [Test]
    public void Week_IsCalendarAnchored_CarryInFromBoundaryDaysOfSameWeek()
    {
        var wednesday = Monday.AddDays(2);
        var context = Context(wednesday, wednesday.AddDays(6), null, Early(AgentA, Monday), Early(AgentA, Monday.AddDays(1)));
        var plan = Plan(context, [.. Days(wednesday, 4).Select(d => Early(AgentA, d))]);

        var result = Evaluator(context, Rule(RuleCounterEvent.WorkedDayInWeek, RuleCalendarPeriod.Week, 6)).Evaluate(plan);

        var finding = result.Findings.ShouldHaveSingleItem();
        finding.Date.ShouldBe(wednesday);
        finding.Observed.ShouldBe(6m);
    }

    [Test]
    public void Week_RollingSevenDaysAcrossWeekBoundary_AreNotCombined()
    {
        var thursday = Monday.AddDays(3);
        var context = Context(thursday, thursday.AddDays(6));
        var plan = Plan(context, [.. Days(thursday, 7).Select(d => Early(AgentA, d))]);

        Evaluator(context, Rule(RuleCounterEvent.WorkedDayInWeek, RuleCalendarPeriod.Week, 6)).Evaluate(plan).Findings.ShouldBeEmpty();
    }

    [Test]
    public void Month_CountsPerCalendarMonth_WithCarryIn()
    {
        var from = new DateOnly(2026, 1, 25);
        var until = new DateOnly(2026, 2, 5);
        var context = Context(from, until, null, Night(AgentA, new DateOnly(2026, 1, 10)), Night(AgentA, new DateOnly(2026, 2, 20)));
        var plan = Plan(context,
            Night(AgentA, new DateOnly(2026, 1, 26)), Night(AgentA, new DateOnly(2026, 1, 27)),
            Night(AgentA, new DateOnly(2026, 2, 1)));

        var finding = Evaluator(context, Rule(RuleCounterEvent.NightShift, RuleCalendarPeriod.Month, 3)).Evaluate(plan)
            .Findings.ShouldHaveSingleItem();

        finding.Date.ShouldBe(from);
        finding.Observed.ShouldBe(3m);
    }

    [Test]
    public void Year_CarryInFromWholeYear_ReachesThreshold()
    {
        var from = new DateOnly(2026, 7, 1);
        var context = Context(from, from.AddDays(30), null,
            Night(AgentA, new DateOnly(2026, 2, 3)), Night(AgentA, new DateOnly(2026, 11, 3)), Night(AgentA, new DateOnly(2025, 12, 31)));
        var plan = Plan(context, Night(AgentA, from.AddDays(4)));

        var rule = Rule(RuleCounterEvent.NightShift, RuleCalendarPeriod.Year, 3);

        Evaluator(context, rule).Evaluate(plan).Findings.ShouldHaveSingleItem().Observed.ShouldBe(3m);
    }

    [Test]
    public void Year_PlanAcrossNewYear_ProducesTwoSlots()
    {
        var from = new DateOnly(2026, 12, 20);
        var context = Context(from, from.AddDays(20));
        var plan = Plan(context, Night(AgentA, new DateOnly(2026, 12, 30)), Night(AgentA, new DateOnly(2027, 1, 2)));

        var result = Evaluator(context, Rule(RuleCounterEvent.NightShift, RuleCalendarPeriod.Year, 1)).Evaluate(plan);

        result.Findings.Select(f => f.Date).ShouldBe([from, new DateOnly(2027, 1, 1)]);
    }

    [Test]
    public void NightShift_CrossMidnightSegmentAgainstWrappingWindow_Counts()
    {
        var saturday = Monday.AddDays(5);
        var context = Context(Monday, Monday.AddDays(6));
        var plan = Plan(context, Segment(AgentA, saturday, new TimeOnly(22, 0), new TimeOnly(7, 0)));

        Evaluator(context, Rule(RuleCounterEvent.NightShift, RuleCalendarPeriod.Week, 1)).Evaluate(plan).HardCount.ShouldBe(1);
    }

    [Test]
    public void NightShift_EarlyEndingAtWindowEnd_DoesNotCount()
    {
        var context = Context(Monday, Monday.AddDays(6));
        var plan = Plan(context, Segment(AgentA, Monday, new TimeOnly(6, 0), new TimeOnly(14, 0)));

        Evaluator(context, Rule(RuleCounterEvent.NightShift, RuleCalendarPeriod.Week, 1)).Evaluate(plan).Findings.ShouldBeEmpty();
    }

    [Test]
    public void NightShift_TwoNightSegmentsOnOneDay_CountTwice()
    {
        var context = Context(Monday, Monday.AddDays(6));
        var plan = Plan(context,
            Segment(AgentA, Monday, new TimeOnly(0, 0), new TimeOnly(3, 0)),
            Segment(AgentA, Monday, new TimeOnly(23, 0), new TimeOnly(23, 59)));

        Evaluator(context, Rule(RuleCounterEvent.NightShift, RuleCalendarPeriod.Week, 2)).Evaluate(plan).HardCount.ShouldBe(1);
    }

    [Test]
    public void ShiftExceedingHours_CrossMidnightDuration_IsStrictlyGreater()
    {
        var context = Context(Monday, Monday.AddDays(6));
        var plan = Plan(context,
            Segment(AgentA, Monday, new TimeOnly(18, 0), new TimeOnly(8, 0)),
            Segment(AgentA, Monday.AddDays(2), new TimeOnly(7, 0), new TimeOnly(20, 0)));

        var finding = Evaluator(context, Rule(RuleCounterEvent.ShiftExceedingHours, RuleCalendarPeriod.Week, 1, 13m)).Evaluate(plan)
            .Findings.ShouldHaveSingleItem();

        finding.Observed.ShouldBe(1m);
    }

    [Test]
    public void ShiftExceedingHours_WithoutHoursThreshold_CountsNothing()
    {
        var context = Context(Monday, Monday.AddDays(6));
        var plan = Plan(context, Segment(AgentA, Monday, new TimeOnly(6, 0), new TimeOnly(22, 0)));

        Evaluator(context, Rule(RuleCounterEvent.ShiftExceedingHours, RuleCalendarPeriod.Week, 1)).Evaluate(plan).Findings.ShouldBeEmpty();
    }

    [Test]
    public void Soft_PenaltyIsWeightTimesCountAboveThresholdPlusOne()
    {
        var context = Context(Monday, Monday.AddDays(6));
        var plan = Plan(context, [.. Days(Monday, 4).Select(d => Night(AgentA, d))]);
        var rule = new PeriodCountRule(Guid.NewGuid(), RuleSeverity.Soft, 1.5, RuleCounterEvent.NightShift, RuleCalendarPeriod.Week, 3);

        var result = Evaluator(context, rule).Evaluate(plan);

        result.HardCount.ShouldBe(0);
        result.SoftPenalty.ShouldBe(3.0);
    }

    [Test]
    public void WouldViolate_OnlyWhenCandidateAddsAnEventAndReachesThreshold()
    {
        var context = Context(Monday, Monday.AddDays(13));
        var plan = Plan(context, Night(AgentA, Monday), Night(AgentA, Monday.AddDays(2)));
        var evaluator = Evaluator(context, Rule(RuleCounterEvent.NightShift, RuleCalendarPeriod.Week, 3));

        WouldViolate(evaluator, plan, Night(AgentA, Monday.AddDays(4))).ShouldBeTrue();
        WouldViolate(evaluator, plan, Early(AgentA, Monday.AddDays(4))).ShouldBeFalse();
        WouldViolate(evaluator, plan, Night(AgentA, Monday.AddDays(8))).ShouldBeFalse();
    }

    private static PeriodCountRule Rule(RuleCounterEvent counterEvent, RuleCalendarPeriod period, int threshold, decimal? hoursThreshold = null)
        => new(Guid.NewGuid(), RuleSeverity.Hard, 1, counterEvent, period, threshold, hoursThreshold);
}
