// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints.Rules;
using static Klacks.UnitTest.ScheduleOptimizer.Constraints.Rules.RuleTestFactory;

namespace Klacks.UnitTest.ScheduleOptimizer.Constraints.Rules;

/// <summary>
/// Contract of IPlanRuleEvaluator: the empty rule set is a true no-op (no findings, no penalty, no
/// allocation), the incremental check is allocation-free, a plan of a foreign context is rejected, and -
/// most important - the slot-incremental WouldViolate agrees exactly with the plan-wide Evaluate on a
/// randomly grown clean plan, so the two code paths can never drift apart.
/// </summary>
[TestFixture]
public class PlanRuleEvaluatorContractTests
{
    private const int RandomSeed = 20261003;
    private const int Placements = 4000;
    private const int AgentCount = 6;
    private const int DayCount = 37;

    [Test]
    public void EmptyRuleSet_ReturnsNoOpEvaluator()
    {
        var context = Context(Monday, Monday.AddDays(6));

        PlanRuleEvaluatorFactory.Create([], context).ShouldBeSameAs(NoOpPlanRuleEvaluator.Instance);
        PlanRuleEvaluatorFactory.Create(null, context).ShouldBeSameAs(NoOpPlanRuleEvaluator.Instance);
    }

    [Test]
    public void EmptyRuleSet_AnyPlan_NoFindingsNoPenaltyNoVeto()
    {
        var context = Context(Monday, Monday.AddDays(6), null, Night(AgentA, Monday.AddDays(-1)));
        var plan = Plan(context, [.. Days(Monday, 7).Select(d => Night(AgentA, d))]);
        var evaluator = PlanRuleEvaluatorFactory.Create([], context);

        var result = evaluator.Evaluate(plan);

        result.ShouldBeSameAs(RuleEvaluation.Empty);
        result.Findings.ShouldBeEmpty();
        result.HardCount.ShouldBe(0);
        result.SoftPenalty.ShouldBe(0d);
        WouldViolate(evaluator, plan, Night(AgentA, Monday.AddDays(3))).ShouldBeFalse();
    }

    [Test]
    public void EmptyRuleSet_EvaluateAndWouldViolate_DoNotAllocate()
    {
        var context = Context(Monday, Monday.AddDays(6));
        var plan = Plan(context, Night(AgentA, Monday));
        var evaluator = PlanRuleEvaluatorFactory.Create([], context);
        var candidate = Candidate(plan, Night(AgentA, Monday.AddDays(1)));
        evaluator.Evaluate(plan);
        evaluator.WouldViolate(plan, 0, 1, candidate);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            evaluator.Evaluate(plan);
            evaluator.WouldViolate(plan, 0, 1, candidate);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0);
    }

    [Test]
    public void WithRules_CleanPlan_ReturnsSharedEmptyEvaluation()
    {
        var context = Context(Monday, Monday.AddDays(6));
        var plan = Plan(context, Early(AgentA, Monday));

        Evaluator(context, HardRules()).Evaluate(plan).ShouldBeSameAs(RuleEvaluation.Empty);
    }

    [Test]
    public void WithRules_WouldViolate_DoesNotAllocate()
    {
        var context = Context(Monday, Monday.AddDays(36), [Agent(AgentA), Agent(AgentB)], Night(AgentA, Monday.AddDays(-1)));
        var plan = Plan(context, [.. Days(Monday, 30).Where((_, i) => i % 3 != 2).Select(d => Night(AgentA, d))]);
        var evaluator = Evaluator(context, [.. HardRules(), FairnessRule()]);
        var candidate = Candidate(plan, Early(AgentA, Monday.AddDays(5)));
        evaluator.WouldViolate(plan, 0, 5, candidate);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            evaluator.WouldViolate(plan, 0, i % DayCount, candidate);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0);
    }

    [Test]
    public void PlanOfOtherContext_IsRejected()
    {
        var context = Context(Monday, Monday.AddDays(6));
        var other = Context(Monday, Monday.AddDays(6));
        var evaluator = Evaluator(context, HardRules());

        Should.Throw<ArgumentException>(() => evaluator.Evaluate(new RulePlan(other)));
    }

    [Test]
    public void WouldViolate_AgreesWithPlanWideEvaluate_OnRandomCleanPlan()
    {
        var agents = Enumerable.Range(0, AgentCount).Select(i => Agent($"agent-{i}")).ToList();
        var from = new DateOnly(2026, 6, 25);
        var until = from.AddDays(DayCount - 1);
        var random = new Random(RandomSeed);
        var boundary = new List<RuleSegment>();
        foreach (var agent in agents)
        {
            for (var offset = 1; offset <= 4; offset++)
            {
                var allowNight = offset == 1;
                if (random.NextDouble() < 0.5)
                {
                    boundary.Add(RandomShift(random, agent.Id, from.AddDays(-offset), allowNight));
                }

                if (random.NextDouble() < 0.5)
                {
                    boundary.Add(RandomShift(random, agent.Id, until.AddDays(offset), allowNight));
                }
            }
        }

        var context = new RuleEvaluationContext(from, until, agents, boundary);
        var plan = new RulePlan(context);
        var evaluator = Evaluator(context, [.. HardRules(), FairnessRule()]);
        evaluator.Evaluate(plan).HardCount.ShouldBe(0, "the boundary alone must not violate, otherwise the clean-plan premise fails");

        var vetoes = 0;
        var accepted = 0;
        for (var i = 0; i < Placements; i++)
        {
            var agentIndex = random.Next(AgentCount);
            var dayIndex = random.Next(DayCount);
            var candidate = random.Next(4) == 0
                ? RuleDay.Free
                : Candidate(plan, RandomShift(random, agents[agentIndex].Id, context.DateAt(dayIndex)));
            var previous = plan.Get(agentIndex, dayIndex);

            var wouldViolate = evaluator.WouldViolate(plan, agentIndex, dayIndex, candidate);
            plan.Set(agentIndex, dayIndex, candidate);
            var violates = evaluator.Evaluate(plan).HardCount > 0;

            wouldViolate.ShouldBe(violates, $"placement {i}: agent {agentIndex}, day {dayIndex}");
            if (violates)
            {
                plan.Set(agentIndex, dayIndex, previous);
                vetoes++;
            }
            else
            {
                accepted++;
            }
        }

        vetoes.ShouldBeGreaterThan(Placements / 10);
        accepted.ShouldBeGreaterThan(Placements / 10);
    }

    private static RuleSegment RandomShift(Random random, string agentId, DateOnly date, bool allowNight = true) => random.Next(allowNight ? 3 : 2) switch
    {
        0 => Early(agentId, date),
        1 => Late(agentId, date),
        _ => Night(agentId, date),
    };

    private static PlanRule[] HardRules() =>
    [
        new MaxConsecutiveOfKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1, RuleShiftKind.Work, 5),
        new ForbiddenTransitionRule(Guid.NewGuid(), RuleSeverity.Hard, 1, RuleShiftKind.Night, RuleShiftKind.Early, 1),
        new RestAfterKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1, RuleShiftKind.Night, 2),
        new PeriodCountRule(Guid.NewGuid(), RuleSeverity.Hard, 1, RuleCounterEvent.NightShift, RuleCalendarPeriod.Week, 3),
    ];

    private static TeamFairnessRule FairnessRule()
        => new(Guid.NewGuid(), 1, FairnessMetric.NightDays, FairnessWindow.PlanPeriod, 1m, true,
            new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday });
}
