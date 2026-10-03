// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Reflection;
using Klacks.ScheduleOptimizer.Constraints.Rules;
using static Klacks.UnitTest.ScheduleOptimizer.Constraints.Rules.RuleTestFactory;

namespace Klacks.UnitTest.ScheduleOptimizer.Constraints.Rules;

/// <summary>
/// Etappe-1 performance gate: 16 agents x 37 days with 5 rules, plan-wide Evaluate below 1 ms and the
/// slot-incremental WouldViolate below 20 microseconds. Each sample times a batch of calls after a warm-up
/// and the median over several samples is asserted, which keeps single scheduler hiccups on a busy CI
/// runner from failing the gate. The hard budget is enforced only when the evaluator assembly is JIT-optimized
/// (Release); a Debug build - as the deploy workflow runs it - only logs and warns.
/// </summary>
[TestFixture]
[Category("Performance")]
public class PlanRuleEvaluatorPerformanceTests
{
    private const int Agents = 16;
    private const int Days = 37;
    private const int Samples = 9;
    private const int WarmupCalls = 2000;
    private const int EvaluateCallsPerSample = 200;
    private const int IncrementalCallsPerSample = 20000;
    private const double PlanWideBudgetMicroseconds = 1000;
    private const double IncrementalBudgetMicroseconds = 20;

    [Test]
    public void Evaluate_And_WouldViolate_StayWithinBudget()
    {
        var (evaluator, plan, candidates) = BuildScenario();

        for (var i = 0; i < WarmupCalls; i++)
        {
            evaluator.Evaluate(plan);
            evaluator.WouldViolate(plan, i % Agents, i % Days, candidates[i % candidates.Length]);
        }

        var planWide = Median(() =>
        {
            for (var i = 0; i < EvaluateCallsPerSample; i++)
            {
                evaluator.Evaluate(plan);
            }
        }, EvaluateCallsPerSample);

        var incremental = Median(() =>
        {
            for (var i = 0; i < IncrementalCallsPerSample; i++)
            {
                evaluator.WouldViolate(plan, i % Agents, (i / Agents) % Days, candidates[i % candidates.Length]);
            }
        }, IncrementalCallsPerSample);

        var optimized = IsJitOptimized();
        TestContext.Out.WriteLine(
            $"Evaluate median: {planWide:F2} us/call; WouldViolate median: {incremental:F3} us/call; JIT optimized: {optimized}");
        if (!optimized)
        {
            if (planWide >= PlanWideBudgetMicroseconds || incremental >= IncrementalBudgetMicroseconds)
            {
                Assert.Warn($"Debug build over budget (Evaluate {planWide:F2} us, WouldViolate {incremental:F3} us); the gate is only enforced on optimized builds.");
            }

            return;
        }

        planWide.ShouldBeLessThan(PlanWideBudgetMicroseconds);
        incremental.ShouldBeLessThan(IncrementalBudgetMicroseconds);
    }

    private static bool IsJitOptimized()
        => typeof(PlanRuleEvaluator).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled != true;

    private static double Median(Action batch, int callsPerBatch)
    {
        var perCall = new double[Samples];
        for (var s = 0; s < Samples; s++)
        {
            var watch = Stopwatch.StartNew();
            batch();
            watch.Stop();
            perCall[s] = watch.Elapsed.TotalMicroseconds / callsPerBatch;
        }

        Array.Sort(perCall);
        return perCall[Samples / 2];
    }

    private static (IPlanRuleEvaluator Evaluator, RulePlan Plan, RuleDay[] Candidates) BuildScenario()
    {
        var random = new Random(16037);
        var from = new DateOnly(2026, 6, 25);
        var agents = Enumerable.Range(0, Agents).Select(i => Agent($"agent-{i}", workloadPercent: 50m + (i * 3))).ToList();
        var boundary = new List<RuleSegment>();
        foreach (var agent in agents)
        {
            for (var day = 1; day <= 120; day++)
            {
                if (random.NextDouble() < 0.6)
                {
                    boundary.Add(Shift(random, agent.Id, from.AddDays(-day)));
                }
            }
        }

        var context = new RuleEvaluationContext(from, from.AddDays(Days - 1), agents, boundary);
        var plan = new RulePlan(context);
        foreach (var agent in agents)
        {
            for (var day = 0; day < Days; day++)
            {
                if (random.NextDouble() < 0.7)
                {
                    plan.TryAdd(Shift(random, agent.Id, from.AddDays(day)));
                }
            }
        }

        PlanRule[] rules =
        [
            new MaxConsecutiveOfKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1, RuleShiftKind.Night, 3),
            new ForbiddenTransitionRule(Guid.NewGuid(), RuleSeverity.Hard, 1, RuleShiftKind.Night, RuleShiftKind.Early, 1),
            new RestAfterKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1, RuleShiftKind.Night, 2),
            new PeriodCountRule(Guid.NewGuid(), RuleSeverity.Hard, 1, RuleCounterEvent.NightShift, RuleCalendarPeriod.Year, 25),
            new TeamFairnessRule(Guid.NewGuid(), 1, FairnessMetric.NightDays, FairnessWindow.Month, 2m, true,
                new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday }),
        ];

        var candidates = new[]
        {
            RuleDay.Free,
            Candidate(plan, Early(agents[0].Id, from)),
            Candidate(plan, Late(agents[0].Id, from)),
            Candidate(plan, Night(agents[0].Id, from)),
        };

        return (PlanRuleEvaluatorFactory.Create(rules, context), plan, candidates);
    }

    private static RuleSegment Shift(Random random, string agentId, DateOnly date) => random.Next(3) switch
    {
        0 => Early(agentId, date),
        1 => Late(agentId, date),
        _ => Night(agentId, date),
    };
}
