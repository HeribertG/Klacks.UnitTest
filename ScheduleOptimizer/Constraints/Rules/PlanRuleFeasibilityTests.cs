// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints.Rules;

namespace Klacks.UnitTest.ScheduleOptimizer.Constraints.Rules;

/// <summary>
/// Tests for the pre-flight feasibility bound: run and rest rules alone and combined, agent scopes, availability,
/// soft rules and plain understaffing (not rule-caused, never reported).
/// </summary>
[TestFixture]
public class PlanRuleFeasibilityTests
{
    private const int Week = 7;

    [Test]
    public void OneAgent_SevenNights_MaxRunThree_IsReported()
    {
        var rule = new MaxConsecutiveOfKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1d, RuleShiftKind.Night, 3);

        var issue = PlanRuleFeasibility.Assess([rule], Context(RuleTestFactory.AgentA), [Nights(1)]).ShouldHaveSingleItem();

        issue.Kind.ShouldBe(RuleShiftKind.Night);
        issue.RuleIds.ShouldBe([rule.RuleId]);
        issue.Demand.ShouldBe(Week);
        issue.Capacity.ShouldBe(6);
        issue.AvailableDays.ShouldBe(Week);
    }

    [Test]
    public void TwoAgents_ShareTheNights_NothingReported()
    {
        var rule = new MaxConsecutiveOfKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1d, RuleShiftKind.Night, 3);

        PlanRuleFeasibility.Assess([rule], Context(RuleTestFactory.AgentA, RuleTestFactory.AgentB), [Nights(1)]).ShouldBeEmpty();
    }

    [TestCase(4, false)]
    [TestCase(5, true)]
    public void RunAndRestCombined_AllowTwoOnTwoOff(int nightsNeeded, bool reported)
    {
        var rules = new PlanRule[]
        {
            new MaxConsecutiveOfKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1d, RuleShiftKind.Night, 2),
            new RestAfterKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1d, RuleShiftKind.Night, 2),
        };
        var slots = Enumerable.Range(0, Week).Select(day => day < nightsNeeded ? 1 : 0).ToList();

        var issues = PlanRuleFeasibility.Assess(rules, Context(RuleTestFactory.AgentA), [new RuleFeasibilityDemand(RuleShiftKind.Night, slots)]);

        issues.Count.ShouldBe(reported ? 1 : 0);
        if (reported)
        {
            issues[0].Capacity.ShouldBe(4);
        }
    }

    [Test]
    public void WorkRule_LimitsEveryKind()
    {
        var rule = new MaxConsecutiveOfKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1d, RuleShiftKind.Work, 5);

        PlanRuleFeasibility.Assess([rule], Context(RuleTestFactory.AgentA), [Nights(1)]).ShouldHaveSingleItem().Capacity.ShouldBe(6);
    }

    [Test]
    public void ScopedRule_LeavesOtherAgentsUnlimited()
    {
        var rule = new MaxConsecutiveOfKindRule(
            Guid.NewGuid(), RuleSeverity.Hard, 1d, RuleShiftKind.Night, 1, new HashSet<string> { RuleTestFactory.AgentA });

        var issue = PlanRuleFeasibility.Assess(
            [rule], Context(RuleTestFactory.AgentA, RuleTestFactory.AgentB), [Nights(2)]).ShouldHaveSingleItem();

        issue.Capacity.ShouldBe(4 + Week);
    }

    [Test]
    public void Unavailability_LowersTheBound()
    {
        var rule = new MaxConsecutiveOfKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1d, RuleShiftKind.Night, 6);
        var slots = Enumerable.Range(0, Week).Select(day => day == 3 ? 0 : 1).ToList();

        var issues = PlanRuleFeasibility.Assess(
            [rule], Context(RuleTestFactory.AgentA), [new RuleFeasibilityDemand(RuleShiftKind.Night, slots)], (_, day) => day != 3);

        issues.ShouldBeEmpty();
    }

    [Test]
    public void SoftRule_IsIgnored()
    {
        var rule = new MaxConsecutiveOfKindRule(Guid.NewGuid(), RuleSeverity.Soft, 1d, RuleShiftKind.Night, 1);

        PlanRuleFeasibility.Assess([rule], Context(RuleTestFactory.AgentA), [Nights(1)]).ShouldBeEmpty();
    }

    [Test]
    public void PlainUnderstaffing_IsNotARuleIssue()
    {
        var rule = new MaxConsecutiveOfKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1d, RuleShiftKind.Night, 3);

        PlanRuleFeasibility.Assess([rule], Context(RuleTestFactory.AgentA), [Nights(2)]).ShouldBeEmpty();
    }

    private static RuleEvaluationContext Context(params string[] agentIds)
        => new(RuleTestFactory.Monday, RuleTestFactory.Monday.AddDays(Week - 1), agentIds.Select(id => RuleTestFactory.Agent(id)).ToList(), []);

    private static RuleFeasibilityDemand Nights(int perDay)
        => new(RuleShiftKind.Night, Enumerable.Repeat(perDay, Week).ToList());
}
