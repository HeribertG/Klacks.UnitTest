// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints.Rules;
using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.ScheduleOptimizer.Constraints.Rules;

/// <summary>
/// Shared builders for the planning-rule evaluator tests: agents, segments, contexts, plans and evaluators.
/// Shift shapes: Early 06:00-14:00 (type 0), Late 14:00-22:00 (type 1), Night 22:00-06:00 (type 2).
/// </summary>
internal static class RuleTestFactory
{
    public const string AgentA = "agent-a";
    public const string AgentB = "agent-b";
    public const string AgentC = "agent-c";

    public static readonly CoreNightWindow DefaultNightWindow = new(new TimeOnly(23, 0), new TimeOnly(6, 0));

    public static readonly DateOnly Monday = new(2026, 7, 13);

    public static RuleAgent Agent(string id, CoreNightWindow? window = null, decimal workloadPercent = 100m)
        => new(id, window ?? DefaultNightWindow, workloadPercent);

    public static RuleAgent AgentWithoutWindow(string id) => new(id, null, 100m);

    public static RuleSegment Segment(string agentId, DateOnly date, TimeOnly start, TimeOnly end, int shiftType = -1)
        => new(agentId, date, start, end, shiftType, (decimal)((end > start ? end - start : TimeSpan.FromHours(24) - (start - end)).TotalHours));

    public static RuleSegment Early(string agentId, DateOnly date) => Segment(agentId, date, new TimeOnly(6, 0), new TimeOnly(14, 0), 0);

    public static RuleSegment Late(string agentId, DateOnly date) => Segment(agentId, date, new TimeOnly(14, 0), new TimeOnly(22, 0), 1);

    public static RuleSegment Night(string agentId, DateOnly date) => Segment(agentId, date, new TimeOnly(22, 0), new TimeOnly(6, 0), 2);

    public static RuleEvaluationContext Context(
        DateOnly from,
        DateOnly until,
        IReadOnlyList<RuleAgent>? agents = null,
        params RuleSegment[] boundary)
        => new(from, until, agents ?? [Agent(AgentA)], boundary);

    public static RulePlan Plan(RuleEvaluationContext context, params RuleSegment[] segments)
    {
        var plan = new RulePlan(context);
        foreach (var segment in segments)
        {
            plan.TryAdd(segment).ShouldBeTrue();
        }

        return plan;
    }

    public static IPlanRuleEvaluator Evaluator(RuleEvaluationContext context, params PlanRule[] rules)
        => PlanRuleEvaluatorFactory.Create(rules, context);

    public static RuleDay Candidate(RulePlan plan, RuleSegment segment)
    {
        plan.Context.TryGetAgentIndex(segment.AgentId, out var agentIndex).ShouldBeTrue();
        return RuleDay.Free.WithSegment(segment, plan.Context.Agents[agentIndex]);
    }

    public static bool WouldViolate(IPlanRuleEvaluator evaluator, RulePlan plan, RuleSegment segment)
    {
        plan.Context.TryGetAgentIndex(segment.AgentId, out var agentIndex).ShouldBeTrue();
        return evaluator.WouldViolate(plan, agentIndex, plan.Context.DayIndexOf(segment.Date), Candidate(plan, segment));
    }

    public static DateOnly[] Days(DateOnly first, int count)
    {
        var days = new DateOnly[count];
        for (var i = 0; i < count; i++)
        {
            days[i] = first.AddDays(i);
        }

        return days;
    }
}
