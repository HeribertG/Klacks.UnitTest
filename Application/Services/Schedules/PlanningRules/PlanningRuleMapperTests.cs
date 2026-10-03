// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules.PlanningRules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.ScheduleOptimizer.Constraints.Rules;

namespace Klacks.UnitTest.Application.Services.Schedules.PlanningRules;

[TestFixture]
public class PlanningRuleMapperTests
{
    [TestCase(null, RuleEnforcementMode.Warn, RuleSeverity.Soft)]
    [TestCase(null, RuleEnforcementMode.Block, RuleSeverity.Hard)]
    [TestCase(RuleEnforcementMode.Block, RuleEnforcementMode.Warn, RuleSeverity.Hard)]
    [TestCase(RuleEnforcementMode.Warn, RuleEnforcementMode.Block, RuleSeverity.Soft)]
    public void CounterRule_SeverityIsOwnEnforcementElseGlobalMode(
        RuleEnforcementMode? ruleEnforcement, RuleEnforcementMode globalMode, RuleSeverity expected)
    {
        var rule = new CounterRule
        {
            Id = Guid.NewGuid(),
            EventType = CounterEventType.ShiftExceedingHours,
            Period = CounterPeriod.Month,
            Threshold = 4,
            HoursThreshold = 12m,
            Enforcement = ruleEnforcement,
        };

        var mapped = PlanningRuleMapper.FromCounterRule(rule, globalMode, null);

        mapped.Severity.ShouldBe(expected);
        mapped.RuleId.ShouldBe(rule.Id);
        mapped.Event.ShouldBe(RuleCounterEvent.ShiftExceedingHours);
        mapped.Period.ShouldBe(RuleCalendarPeriod.Month);
        mapped.Threshold.ShouldBe(4);
        mapped.HoursThreshold.ShouldBe(12m);
        mapped.Weight.ShouldBe(PlanningConstraintDefaults.DefaultCounterRuleSoftWeight);
        mapped.AgentScope.ShouldBeNull();
    }

    [Test]
    public void EveryDomainEnumValue_MapsOntoTheOptimizerValueOfTheSameName()
    {
        foreach (var value in Enum.GetValues<PlanningShiftKind>())
        {
            PlanningRuleMapper.ToShiftKind(value).ToString().ShouldBe(value.ToString());
        }

        foreach (var value in Enum.GetValues<PlanningFairnessMetric>())
        {
            PlanningRuleMapper.ToMetric(value).ToString().ShouldBe(value.ToString());
        }

        foreach (var value in Enum.GetValues<PlanningFairnessWindow>())
        {
            PlanningRuleMapper.ToWindow(value).ToString().ShouldBe(value.ToString());
        }

        foreach (var value in Enum.GetValues<CounterEventType>())
        {
            PlanningRuleMapper.ToCounterEvent(value).ToString().ShouldBe(value.ToString());
        }

        foreach (var value in Enum.GetValues<CounterPeriod>())
        {
            PlanningRuleMapper.ToCalendarPeriod(value).ToString().ShouldBe(value.ToString());
        }

        Enum.GetValues<PlanningShiftKind>().Length.ShouldBe(Enum.GetValues<RuleShiftKind>().Length);
        Enum.GetValues<PlanningFairnessMetric>().Length.ShouldBe(Enum.GetValues<FairnessMetric>().Length);
        Enum.GetValues<PlanningFairnessWindow>().Length.ShouldBe(Enum.GetValues<FairnessWindow>().Length);
    }

    [Test]
    public void UndefinedValue_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => PlanningRuleMapper.ToShiftKind((PlanningShiftKind)0));
        Should.Throw<ArgumentOutOfRangeException>(() => PlanningRuleMapper.ToSeverity((RuleEnforcementMode)7));
    }

    [Test]
    public void Constraint_SequenceKinds_MapWithSeverityWeightAndScope()
    {
        var scope = new HashSet<string>(StringComparer.Ordinal) { "a" };
        var constraint = new PlanningConstraint
        {
            Id = Guid.NewGuid(),
            Kind = PlanningConstraintKind.ForbiddenTransition,
            Severity = PlanningConstraintSeverity.Soft,
            Weight = 3.5,
        };

        var rule = PlanningRuleMapper.FromConstraint(
            constraint, new ForbiddenTransitionParameters(PlanningShiftKind.Night, PlanningShiftKind.Early, 2), scope);

        var transition = rule.ShouldBeOfType<ForbiddenTransitionRule>();
        transition.RuleId.ShouldBe(constraint.Id);
        transition.Severity.ShouldBe(RuleSeverity.Soft);
        transition.Weight.ShouldBe(3.5);
        transition.FromKind.ShouldBe(RuleShiftKind.Night);
        transition.ToKind.ShouldBe(RuleShiftKind.Early);
        transition.WithinDays.ShouldBe(2);
        transition.AgentScope.ShouldBeSameAs(scope);
    }

    [Test]
    public void Constraint_TeamFairness_IsAlwaysSoft()
    {
        var constraint = new PlanningConstraint
        {
            Id = Guid.NewGuid(),
            Kind = PlanningConstraintKind.TeamFairness,
            Severity = PlanningConstraintSeverity.Soft,
            Weight = 1,
        };
        var parameters = new TeamFairnessParameters(
            PlanningFairnessMetric.NightDays, PlanningFairnessWindow.PlanPeriod, 2m, true, new HashSet<DayOfWeek>());

        var rule = PlanningRuleMapper.FromConstraint(constraint, parameters, null).ShouldBeOfType<TeamFairnessRule>();

        rule.Severity.ShouldBe(RuleSeverity.Soft);
        rule.Metric.ShouldBe(FairnessMetric.NightDays);
        rule.ProRata.ShouldBeTrue();
    }

    [Test]
    public void Constraint_WithParametersOfAnotherKind_Throws()
    {
        var constraint = new PlanningConstraint { Kind = PlanningConstraintKind.RestAfterKind, Severity = PlanningConstraintSeverity.Hard };

        Should.Throw<ArgumentException>(() => PlanningRuleMapper.FromConstraint(
            constraint, new MaxConsecutiveOfKindParameters(PlanningShiftKind.Night, 3), null));
    }
}
