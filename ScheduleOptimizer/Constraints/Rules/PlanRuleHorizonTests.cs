// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints.Rules;

namespace Klacks.UnitTest.ScheduleOptimizer.Constraints.Rules;

[TestFixture]
public class PlanRuleHorizonTests
{
    private static readonly DateOnly From = new(2026, 2, 10);
    private static readonly DateOnly Until = new(2026, 2, 20);

    [Test]
    public void NoRules_LookNowhereOutsideThePeriod()
    {
        PlanRuleHorizon.NeighborDays([]).ShouldBe(0);
        PlanRuleHorizon.BoundaryWindow([], From, Until).ShouldBe((From, Until));
    }

    [Test]
    public void SequenceRules_UseTheLargestNeighbourReach()
    {
        PlanRule[] rules =
        [
            new MaxConsecutiveOfKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1d, RuleShiftKind.Night, 4),
            new ForbiddenTransitionRule(Guid.NewGuid(), RuleSeverity.Hard, 1d, RuleShiftKind.Night, RuleShiftKind.Early, 2),
            new RestAfterKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1d, RuleShiftKind.Night, 2),
        ];

        PlanRuleHorizon.NeighborDays(rules).ShouldBe(4);
        PlanRuleHorizon.BoundaryWindow(rules, From, Until).ShouldBe((From.AddDays(-4), Until.AddDays(4)));
    }

    [TestCase(RuleCalendarPeriod.Week, "2026-02-09", "2026-02-22")]
    [TestCase(RuleCalendarPeriod.Month, "2026-02-01", "2026-02-28")]
    [TestCase(RuleCalendarPeriod.Year, "2026-01-01", "2026-12-31")]
    public void PeriodCount_ReachesTheWholeCalendarPeriod(RuleCalendarPeriod period, string expectedFrom, string expectedUntil)
    {
        var rule = new PeriodCountRule(Guid.NewGuid(), RuleSeverity.Soft, 1d, RuleCounterEvent.WorkedDayInWeek, period, 6);

        var window = PlanRuleHorizon.BoundaryWindow([rule], From, Until);

        window.ShouldBe((DateOnly.ParseExact(expectedFrom, "yyyy-MM-dd"), DateOnly.ParseExact(expectedUntil, "yyyy-MM-dd")));
    }

    [Test]
    public void TeamFairness_NeedsNoBoundary()
    {
        var rule = new TeamFairnessRule(
            Guid.NewGuid(), 1d, FairnessMetric.NightDays, FairnessWindow.Month, 1m, true, new HashSet<DayOfWeek>());

        PlanRuleHorizon.BoundaryWindow([rule], From, Until).ShouldBe((From, Until));
    }
}
