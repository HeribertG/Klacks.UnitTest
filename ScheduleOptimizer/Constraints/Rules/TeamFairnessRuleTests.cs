// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints.Rules;
using static Klacks.UnitTest.ScheduleOptimizer.Constraints.Rules.RuleTestFactory;

namespace Klacks.UnitTest.ScheduleOptimizer.Constraints.Rules;

/// <summary>
/// TeamFairness rule: spread across the scope, pro-rata scaling by workload, zero-workload exclusion,
/// configurable weekend days, calendar windows clipped to the plan and the soft-only guarantee.
/// </summary>
[TestFixture]
public class TeamFairnessRuleTests
{
    private static readonly IReadOnlySet<DayOfWeek> SaturdaySunday = new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday };

    [Test]
    public void SpreadAboveMax_IsSoftFindingWithWeightedExcess()
    {
        var context = Context(Monday, Monday.AddDays(13), [Agent(AgentA), Agent(AgentB), Agent(AgentC)]);
        var plan = Plan(context,
            Night(AgentA, Monday), Night(AgentA, Monday.AddDays(4)), Night(AgentA, Monday.AddDays(8)),
            Night(AgentB, Monday.AddDays(1)),
            Night(AgentC, Monday.AddDays(2)));

        var result = Evaluator(context, Rule(FairnessMetric.NightDays, FairnessWindow.PlanPeriod, 1m, proRata: false, weight: 2)).Evaluate(plan);

        var finding = result.Findings.ShouldHaveSingleItem();
        finding.AgentId.ShouldBeNull();
        finding.Severity.ShouldBe(RuleSeverity.Soft);
        finding.Observed.ShouldBe(2m);
        result.HardCount.ShouldBe(0);
        result.SoftPenalty.ShouldBe(2.0);
    }

    [Test]
    public void SpreadWithinMax_NoFinding()
    {
        var context = Context(Monday, Monday.AddDays(13), [Agent(AgentA), Agent(AgentB)]);
        var plan = Plan(context, Night(AgentA, Monday), Night(AgentA, Monday.AddDays(4)), Night(AgentB, Monday.AddDays(1)));

        Evaluator(context, Rule(FairnessMetric.NightDays, FairnessWindow.PlanPeriod, 1m, proRata: false)).Evaluate(plan).Findings.ShouldBeEmpty();
    }

    [Test]
    public void ProRata_HalfTimeAgentWithHalfTheNights_IsFair()
    {
        var context = Context(Monday, Monday.AddDays(13), [Agent(AgentA), Agent(AgentB, workloadPercent: 50m)]);
        var plan = Plan(context,
            Night(AgentA, Monday), Night(AgentA, Monday.AddDays(3)), Night(AgentA, Monday.AddDays(6)), Night(AgentA, Monday.AddDays(9)),
            Night(AgentB, Monday.AddDays(1)), Night(AgentB, Monday.AddDays(5)));

        Evaluator(context, Rule(FairnessMetric.NightDays, FairnessWindow.PlanPeriod, 0m, proRata: true)).Evaluate(plan).Findings.ShouldBeEmpty();
        Evaluator(context, Rule(FairnessMetric.NightDays, FairnessWindow.PlanPeriod, 0m, proRata: false)).Evaluate(plan).Findings.ShouldHaveSingleItem().Observed.ShouldBe(2m);
    }

    [Test]
    public void ProRata_ZeroWorkloadAgent_IsLeftOut()
    {
        var context = Context(Monday, Monday.AddDays(6), [Agent(AgentA), Agent(AgentB), Agent(AgentC, workloadPercent: 0m)]);
        var plan = Plan(context, Night(AgentA, Monday), Night(AgentB, Monday.AddDays(1)),
            Night(AgentC, Monday.AddDays(2)), Night(AgentC, Monday.AddDays(4)), Night(AgentC, Monday.AddDays(6)));

        Evaluator(context, Rule(FairnessMetric.NightDays, FairnessWindow.PlanPeriod, 0m, proRata: true)).Evaluate(plan).Findings.ShouldBeEmpty();
    }

    [Test]
    public void WeekendDays_AreTakenFromTheRule_NotAssumed()
    {
        var friday = Monday.AddDays(4);
        var fridaySaturday = new HashSet<DayOfWeek> { DayOfWeek.Friday, DayOfWeek.Saturday };
        var context = Context(Monday, Monday.AddDays(6), [Agent(AgentA), Agent(AgentB)]);
        var plan = Plan(context, Early(AgentA, friday), Early(AgentB, Monday.AddDays(6)));

        Evaluator(context, Rule(FairnessMetric.WeekendDays, FairnessWindow.PlanPeriod, 0m, proRata: false, weekend: fridaySaturday))
            .Evaluate(plan).Findings.ShouldHaveSingleItem().Observed.ShouldBe(1m);
        Evaluator(context, Rule(FairnessMetric.WeekendDays, FairnessWindow.PlanPeriod, 0m, proRata: false, weekend: SaturdaySunday))
            .Evaluate(plan).Findings.ShouldHaveSingleItem().Observed.ShouldBe(1m);
    }

    [Test]
    public void WeekWindow_IsCalendarAnchoredAndClippedToPlan()
    {
        var wednesday = Monday.AddDays(2);
        var context = Context(wednesday, wednesday.AddDays(6), [Agent(AgentA), Agent(AgentB)]);
        var plan = Plan(context, Early(AgentA, wednesday), Early(AgentB, Monday.AddDays(7)));

        var result = Evaluator(context, Rule(FairnessMetric.WorkedDays, FairnessWindow.Week, 0m, proRata: false)).Evaluate(plan);

        result.Findings.Select(f => f.Date).ShouldBe([wednesday, Monday.AddDays(7)]);
    }

    [Test]
    public void HardSeverityViaWithExpression_IsStillSoftAndNeverVetoes()
    {
        var context = Context(Monday, Monday.AddDays(6), [Agent(AgentA), Agent(AgentB)]);
        var plan = Plan(context, Night(AgentA, Monday), Night(AgentA, Monday.AddDays(2)));
        var hard = Rule(FairnessMetric.NightDays, FairnessWindow.PlanPeriod, 0m, proRata: false) with { Severity = RuleSeverity.Hard };
        var evaluator = Evaluator(context, hard);

        var result = evaluator.Evaluate(plan);

        result.HardCount.ShouldBe(0);
        result.Findings.ShouldHaveSingleItem().Severity.ShouldBe(RuleSeverity.Soft);
        WouldViolate(evaluator, plan, Night(AgentA, Monday.AddDays(4))).ShouldBeFalse();
    }

    [Test]
    public void Constructor_AlwaysProducesSoftRule()
    {
        Rule(FairnessMetric.NightDays, FairnessWindow.PlanPeriod, 1m, proRata: true).Severity.ShouldBe(RuleSeverity.Soft);
    }

    private static TeamFairnessRule Rule(
        FairnessMetric metric,
        FairnessWindow window,
        decimal maxSpread,
        bool proRata,
        double weight = 1,
        IReadOnlySet<DayOfWeek>? weekend = null)
        => new(Guid.NewGuid(), weight, metric, window, maxSpread, proRata, weekend ?? SaturdaySunday);
}
