// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints.Rules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Rules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Constraints.Rules;

/// <summary>
/// The per-agent decomposition must reproduce the plan-wide evaluation: hard Excess per (rule, agent) exactly, the
/// soft penalty up to floating-point summation order. Plans come from the bitmap fixture (boundary nights, carry-in,
/// two-work days, agents without a night window, scoped rules).
/// </summary>
[TestFixture]
public class IncrementalPlanRuleEvaluatorTests
{
    private const double Tolerance = 1e-9;
    private static readonly int[] Seeds = Enumerable.Range(1, 15).ToArray();

    [TestCaseSource(nameof(Seeds))]
    public void Decomposition_MatchesPlanWideEvaluate(int seed)
    {
        var input = PlanningRuleBitmapFixture.Build(seed, PlanningRuleBitmapFixture.MixedRules());
        var context = PlanningRuleBitmapFixture.Context(input);
        var evaluator = PlanRuleEvaluatorFactory.CreateIncremental(input.Rules!.Rules, context);
        var plan = PlanningRuleBitmapFixture.OraclePlan(context, input, BitmapBuilder.Build(input));
        var evaluation = evaluator.Evaluate(plan);
        var expectedHard = PlanningRuleBitmapFixture.HardExcess(evaluation);

        var excess = new decimal[evaluator.HardRuleCount];
        var windowValues = new decimal[context.AgentCount][];
        var softPenalty = 0d;
        for (var agent = 0; agent < context.AgentCount; agent++)
        {
            evaluator.HardExcessOf(plan, agent, excess);
            for (var rule = 0; rule < excess.Length; rule++)
            {
                var key = (evaluator.HardRuleAt(rule).RuleId, (string?)context.Agents[agent].Id);
                excess[rule].ShouldBe(expectedHard.GetValueOrDefault(key), $"agent {agent} rule {rule}");
            }

            windowValues[agent] = new decimal[evaluator.TeamWindowCount];
            softPenalty += evaluator.AgentSoftPenalty(plan, agent, windowValues[agent]);
        }

        softPenalty += evaluator.TeamSoftPenalty(windowValues);
        softPenalty.ShouldBe(evaluation.SoftPenalty, Tolerance);
        evaluation.HardCount.ShouldBeGreaterThan(0, "the fixture must make hard rules fire");
        evaluation.SoftPenalty.ShouldBeGreaterThan(0d, "the fixture must make soft rules fire");
    }

    [Test]
    public void EmptyRuleSet_IsTheNoOpEvaluator()
    {
        var context = RuleTestFactory.Context(RuleTestFactory.Monday, RuleTestFactory.Monday.AddDays(6));

        var evaluator = PlanRuleEvaluatorFactory.CreateIncremental([], context);

        evaluator.ShouldBeSameAs(NoOpPlanRuleEvaluator.Instance);
        evaluator.HardRuleCount.ShouldBe(0);
        evaluator.HasSoftRules.ShouldBeFalse();
        evaluator.TeamSoftPenalty([]).ShouldBe(0d);
    }
}
