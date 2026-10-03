// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints.Rules;

namespace Klacks.UnitTest.ScheduleOptimizer.Constraints.Rules;

/// <summary>
/// Owner decision 7: the sequence and fairness rules see a Night day only when a segment overlaps the night
/// window (23:00-06:00 here) by more than the agent's NightRuleMinOverlapMinutes, while PeriodCount night
/// counting keeps "any overlap" (CounterRule parity).
/// </summary>
[TestFixture]
public class NightRuleMinOverlapTests
{
    private const int DefaultMinOverlap = 60;
    private const int FromFirstMinute = 0;

    [TestCase(5, 0, 13, 0, DefaultMinOverlap, false)]
    [TestCase(5, 0, 13, 0, FromFirstMinute, true)]
    [TestCase(22, 0, 6, 0, DefaultMinOverlap, true)]
    [TestCase(22, 0, 6, 0, FromFirstMinute, true)]
    [TestCase(15, 0, 23, 30, DefaultMinOverlap, false)]
    public void NightFlag_FollowsTheMinimumOverlap(int startHour, int startMinute, int endHour, int endMinute, int minOverlap, bool isNight)
    {
        var agent = new RuleAgent(RuleTestFactory.AgentA, RuleTestFactory.DefaultNightWindow, 100m, minOverlap);
        var segment = RuleTestFactory.Segment(
            RuleTestFactory.AgentA, RuleTestFactory.Monday, new TimeOnly(startHour, startMinute), new TimeOnly(endHour, endMinute));

        var day = RuleDay.Free.WithSegment(segment, agent);

        day.Has(RuleShiftKind.Night).ShouldBe(isNight);
        day.NightSegmentCount.ShouldBe(1, "PeriodCount keeps counting any overlap");
    }

    [Test]
    public void EarlyWeekFromFive_IsNoNightRun_UnderTheDefault_ButStillCountsForCounterRules()
    {
        var days = RuleTestFactory.Days(RuleTestFactory.Monday, 5);
        var segments = days
            .Select(date => RuleTestFactory.Segment(RuleTestFactory.AgentA, date, new TimeOnly(5, 0), new TimeOnly(13, 0), 0))
            .ToArray();
        var run = new MaxConsecutiveOfKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1d, RuleShiftKind.Night, 2);
        var count = new PeriodCountRule(Guid.NewGuid(), RuleSeverity.Hard, 1d, RuleCounterEvent.NightShift, RuleCalendarPeriod.Week, 5);

        var findings = Evaluate(DefaultMinOverlap, segments, run, count);

        findings.ShouldHaveSingleItem().RuleId.ShouldBe(count.RuleId);
        Evaluate(FromFirstMinute, segments, run, count).Select(f => f.RuleId).ShouldBe([run.RuleId, count.RuleId], ignoreOrder: true);
    }

    private static IReadOnlyList<RuleFinding> Evaluate(int minOverlap, RuleSegment[] segments, params PlanRule[] rules)
    {
        var agent = new RuleAgent(RuleTestFactory.AgentA, RuleTestFactory.DefaultNightWindow, 100m, minOverlap);
        var context = RuleTestFactory.Context(RuleTestFactory.Monday, RuleTestFactory.Monday.AddDays(6), [agent]);
        var plan = RuleTestFactory.Plan(context, segments);
        return RuleTestFactory.Evaluator(context, rules).Evaluate(plan).Findings;
    }
}
