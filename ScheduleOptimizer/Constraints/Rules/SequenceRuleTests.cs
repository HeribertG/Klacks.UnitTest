// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints.Rules;
using static Klacks.UnitTest.ScheduleOptimizer.Constraints.Rules.RuleTestFactory;

namespace Klacks.UnitTest.ScheduleOptimizer.Constraints.Rules;

/// <summary>
/// Positive/negative cases for the three sequence rules (MaxConsecutiveOfKind, ForbiddenTransition,
/// RestAfterKind), including period edges seen through boundary days, break days as free days and the
/// night classification by night-window overlap.
/// </summary>
[TestFixture]
public class SequenceRuleTests
{
    private static readonly DateOnly From = Monday;
    private static readonly DateOnly Until = Monday.AddDays(13);

    [Test]
    public void MaxConsecutive_RunAtLimit_NoFinding()
    {
        var context = Context(From, Until);
        var plan = Plan(context, Night(AgentA, From), Night(AgentA, From.AddDays(1)), Night(AgentA, From.AddDays(2)));

        var result = Evaluator(context, MaxNights(3)).Evaluate(plan);

        result.ShouldBeSameAs(RuleEvaluation.Empty);
    }

    [Test]
    public void MaxConsecutive_RunAboveLimit_ReportsAtRunStart()
    {
        var context = Context(From, Until);
        var plan = Plan(context, [.. Days(From.AddDays(2), 4).Select(d => Night(AgentA, d))]);

        var result = Evaluator(context, MaxNights(3)).Evaluate(plan);

        var finding = result.Findings.ShouldHaveSingleItem();
        finding.AgentId.ShouldBe(AgentA);
        finding.Date.ShouldBe(From.AddDays(2));
        finding.Observed.ShouldBe(4m);
        finding.Limit.ShouldBe(3m);
        result.HardCount.ShouldBe(1);
    }

    [Test]
    public void MaxConsecutive_RunContinuesFromBoundaryBefore_ReportsAtBoundaryStart()
    {
        var context = Context(From, Until, null, Night(AgentA, From.AddDays(-2)), Night(AgentA, From.AddDays(-1)));
        var plan = Plan(context, Night(AgentA, From), Night(AgentA, From.AddDays(1)));

        var finding = Evaluator(context, MaxNights(3)).Evaluate(plan).Findings.ShouldHaveSingleItem();

        finding.Date.ShouldBe(From.AddDays(-2));
        finding.Observed.ShouldBe(4m);
    }

    [Test]
    public void MaxConsecutive_RunContinuesIntoBoundaryAfter_IsReported()
    {
        var context = Context(From, Until, null, Night(AgentA, Until.AddDays(1)), Night(AgentA, Until.AddDays(2)));
        var plan = Plan(context, Night(AgentA, Until.AddDays(-1)), Night(AgentA, Until));

        Evaluator(context, MaxNights(3)).Evaluate(plan).HardCount.ShouldBe(1);
    }

    [Test]
    public void MaxConsecutive_BreakDayCountsAsFree_RunIsSplit()
    {
        var context = Context(From, Until);
        var plan = Plan(context,
            Night(AgentA, From), Night(AgentA, From.AddDays(1)),
            Night(AgentA, From.AddDays(3)), Night(AgentA, From.AddDays(4)));

        Evaluator(context, MaxNights(3)).Evaluate(plan).Findings.ShouldBeEmpty();
    }

    [Test]
    public void MaxConsecutive_WouldViolate_DetectsRunExtensionOnly()
    {
        var context = Context(From, Until);
        var plan = Plan(context, Night(AgentA, From), Night(AgentA, From.AddDays(1)), Night(AgentA, From.AddDays(2)));
        var evaluator = Evaluator(context, MaxNights(3));

        WouldViolate(evaluator, plan, Night(AgentA, From.AddDays(3))).ShouldBeTrue();
        WouldViolate(evaluator, plan, Early(AgentA, From.AddDays(3))).ShouldBeFalse();
        WouldViolate(evaluator, plan, Night(AgentA, From.AddDays(4))).ShouldBeFalse();
    }

    [Test]
    public void MaxConsecutive_Soft_AddsWeightedExcessWithoutHardCount()
    {
        var context = Context(From, Until);
        var plan = Plan(context, [.. Days(From, 5).Select(d => Night(AgentA, d))]);
        var rule = new MaxConsecutiveOfKindRule(Guid.NewGuid(), RuleSeverity.Soft, 2.5, RuleShiftKind.Night, 3);
        var evaluator = Evaluator(context, rule);

        var result = evaluator.Evaluate(plan);

        result.HardCount.ShouldBe(0);
        result.SoftPenalty.ShouldBe(5.0);
        WouldViolate(evaluator, plan, Night(AgentA, From.AddDays(5))).ShouldBeFalse();
    }

    [Test]
    public void MaxConsecutive_AgentScope_SkipsOtherAgents()
    {
        var context = Context(From, Until, [Agent(AgentA), Agent(AgentB)]);
        var plan = Plan(context, [.. Days(From, 4).SelectMany(d => new[] { Night(AgentA, d), Night(AgentB, d) })]);
        var rule = new MaxConsecutiveOfKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1, RuleShiftKind.Night, 3, new HashSet<string> { AgentB });

        Evaluator(context, rule).Evaluate(plan).Findings.ShouldHaveSingleItem().AgentId.ShouldBe(AgentB);
    }

    [Test]
    public void ForbiddenTransition_NightThenEarlyNextDay_IsReported()
    {
        var context = Context(From, Until);
        var plan = Plan(context, Night(AgentA, From), Early(AgentA, From.AddDays(1)));

        var finding = Evaluator(context, NightToEarly(1)).Evaluate(plan).Findings.ShouldHaveSingleItem();

        finding.Date.ShouldBe(From);
        finding.Observed.ShouldBe(1m);
    }

    [Test]
    public void ForbiddenTransition_GapLargerThanWithinDays_NoFinding()
    {
        var context = Context(From, Until);
        var plan = Plan(context, Night(AgentA, From), Early(AgentA, From.AddDays(2)));

        Evaluator(context, NightToEarly(1)).Evaluate(plan).Findings.ShouldBeEmpty();
        Evaluator(context, NightToEarly(2)).Evaluate(plan).HardCount.ShouldBe(1);
    }

    [Test]
    public void ForbiddenTransition_FromDayInBoundary_IsReportedAtBoundaryDate()
    {
        var context = Context(From, Until, null, Night(AgentA, From.AddDays(-1)));
        var plan = Plan(context, Early(AgentA, From));

        Evaluator(context, NightToEarly(1)).Evaluate(plan).Findings.ShouldHaveSingleItem().Date.ShouldBe(From.AddDays(-1));
    }

    [Test]
    public void ForbiddenTransition_ToDayInBoundaryAfter_IsReported()
    {
        var context = Context(From, Until, null, Early(AgentA, Until.AddDays(1)));
        var plan = Plan(context, Night(AgentA, Until));

        Evaluator(context, NightToEarly(1)).Evaluate(plan).HardCount.ShouldBe(1);
    }

    [Test]
    public void ForbiddenTransition_WouldViolate_ChecksBothDirections()
    {
        var context = Context(From, Until);
        var plan = Plan(context, Night(AgentA, From.AddDays(3)), Early(AgentA, From.AddDays(6)));
        var evaluator = Evaluator(context, NightToEarly(1));

        WouldViolate(evaluator, plan, Early(AgentA, From.AddDays(4))).ShouldBeTrue();
        WouldViolate(evaluator, plan, Night(AgentA, From.AddDays(5))).ShouldBeTrue();
        WouldViolate(evaluator, plan, Late(AgentA, From.AddDays(4))).ShouldBeFalse();
    }

    [Test]
    public void NightClassification_LateShiftOverlappingNightWindow_CountsAsNight()
    {
        var context = Context(From, Until);
        var lateIntoNight = Segment(AgentA, From, new TimeOnly(15, 0), new TimeOnly(23, 30), 1);
        var plan = Plan(context, lateIntoNight, Early(AgentA, From.AddDays(1)));

        Evaluator(context, NightToEarly(1)).Evaluate(plan).HardCount.ShouldBe(1);
    }

    [Test]
    public void NightClassification_NightTypeOutsideWindow_IsNotNight()
    {
        var context = Context(From, Until);
        var eveningNightType = Segment(AgentA, From, new TimeOnly(18, 0), new TimeOnly(22, 0), 2);
        var plan = Plan(context, eveningNightType, Early(AgentA, From.AddDays(1)));

        Evaluator(context, NightToEarly(1)).Evaluate(plan).Findings.ShouldBeEmpty();
    }

    [Test]
    public void NightClassification_WithoutWindow_FallsBackToShiftType()
    {
        var context = Context(From, Until, [AgentWithoutWindow(AgentA)]);
        var eveningNightType = Segment(AgentA, From, new TimeOnly(18, 0), new TimeOnly(22, 0), 2);
        var lateIntoNight = Segment(AgentA, From.AddDays(3), new TimeOnly(15, 0), new TimeOnly(23, 30), 1);
        var plan = Plan(context, eveningNightType, Early(AgentA, From.AddDays(1)), lateIntoNight, Early(AgentA, From.AddDays(4)));

        Evaluator(context, NightToEarly(1)).Evaluate(plan).Findings.ShouldHaveSingleItem().Date.ShouldBe(From);
    }

    [Test]
    public void RestAfterKind_TwoFreeDaysAfterNightBlock_NoFinding()
    {
        var context = Context(From, Until);
        var plan = Plan(context, Night(AgentA, From), Night(AgentA, From.AddDays(1)), Early(AgentA, From.AddDays(4)));

        Evaluator(context, RestAfterNights(2)).Evaluate(plan).Findings.ShouldBeEmpty();
    }

    [Test]
    public void RestAfterKind_WorkInsideRestWindow_IsReportedAtBlockEnd()
    {
        var context = Context(From, Until);
        var plan = Plan(context, Night(AgentA, From), Night(AgentA, From.AddDays(1)), Early(AgentA, From.AddDays(3)));

        var finding = Evaluator(context, RestAfterNights(2)).Evaluate(plan).Findings.ShouldHaveSingleItem();

        finding.Date.ShouldBe(From.AddDays(1));
        finding.Observed.ShouldBe(1m);
        finding.Limit.ShouldBe(2m);
    }

    [Test]
    public void RestAfterKind_BreakInsideRestWindow_CountsAsFree()
    {
        var context = Context(From, Until);
        var plan = Plan(context, Night(AgentA, From), Late(AgentA, From.AddDays(3)));

        Evaluator(context, RestAfterNights(2)).Evaluate(plan).Findings.ShouldBeEmpty();
    }

    [Test]
    public void RestAfterKind_WorkInBoundaryAfter_IsReported()
    {
        var context = Context(From, Until, null, Early(AgentA, Until.AddDays(1)));
        var plan = Plan(context, Night(AgentA, Until));

        Evaluator(context, RestAfterNights(2)).Evaluate(plan).HardCount.ShouldBe(1);
    }

    [Test]
    public void RestAfterKind_NightBlockEndingInBoundaryBefore_IsReported()
    {
        var context = Context(From, Until, null, Night(AgentA, From.AddDays(-1)));
        var plan = Plan(context, Early(AgentA, From.AddDays(1)));

        Evaluator(context, RestAfterNights(2)).Evaluate(plan).Findings.ShouldHaveSingleItem().Date.ShouldBe(From.AddDays(-1));
    }

    [Test]
    public void RestAfterKind_WouldViolate_RestDayVersusBlockExtension()
    {
        var context = Context(From, Until);
        var plan = Plan(context, Night(AgentA, From), Night(AgentA, From.AddDays(1)));
        var evaluator = Evaluator(context, RestAfterNights(2));

        WouldViolate(evaluator, plan, Early(AgentA, From.AddDays(2))).ShouldBeTrue();
        WouldViolate(evaluator, plan, Night(AgentA, From.AddDays(2))).ShouldBeFalse();
        WouldViolate(evaluator, plan, Early(AgentA, From.AddDays(4))).ShouldBeFalse();
    }

    [Test]
    public void RestAfterKind_ViolationEntirelyBeforePeriod_IsNotReportedOnEmptyPlan()
    {
        var context = Context(From, Until, null, Night(AgentA, From.AddDays(-2)), Early(AgentA, From.AddDays(-1)));
        var plan = new RulePlan(context);
        var evaluator = Evaluator(context, RestAfterNights(2));

        var result = evaluator.Evaluate(plan);

        result.Findings.ShouldBeEmpty();
        result.HardCount.ShouldBe(0);
        WouldViolate(evaluator, plan, Early(AgentA, From.AddDays(2))).ShouldBeFalse();
        evaluator.WouldViolate(plan, 0, 0, RuleDay.Free).ShouldBeFalse();
    }

    [Test]
    public void RestAfterKind_BlockEndedBeforePeriod_StillReportsWorkInsidePeriod()
    {
        var context = Context(From, Until, null, Night(AgentA, From.AddDays(-2)), Early(AgentA, From.AddDays(-1)));
        var plan = Plan(context, Early(AgentA, From));
        var evaluator = Evaluator(context, RestAfterNights(2));

        var finding = evaluator.Evaluate(plan).Findings.ShouldHaveSingleItem();

        finding.Date.ShouldBe(From.AddDays(-2));
        finding.Observed.ShouldBe(1m);
        evaluator.WouldViolate(plan, 0, 0, Candidate(plan, Early(AgentA, From))).ShouldBeTrue();
        evaluator.WouldViolate(plan, 0, 0, RuleDay.Free).ShouldBeFalse();
    }

    [Test]
    public void SequenceRules_BoundaryOnlyViolationsOutsidePeriod_AreNotReportedOnEmptyPlan()
    {
        var context = Context(From, Until, null, Night(AgentA, From.AddDays(-3)), Early(AgentA, From.AddDays(-2)), Night(AgentA, Until.AddDays(2)), Early(AgentA, Until.AddDays(3)));
        var plan = new RulePlan(context);

        var result = Evaluator(context, MaxNights(0), NightToEarly(3), RestAfterNights(3)).Evaluate(plan);

        result.Findings.ShouldBeEmpty();
    }

    private static MaxConsecutiveOfKindRule MaxNights(int maxRun)
        => new(Guid.NewGuid(), RuleSeverity.Hard, 1, RuleShiftKind.Night, maxRun);

    private static ForbiddenTransitionRule NightToEarly(int withinDays)
        => new(Guid.NewGuid(), RuleSeverity.Hard, 1, RuleShiftKind.Night, RuleShiftKind.Early, withinDays);

    private static RestAfterKindRule RestAfterNights(int freeDays)
        => new(Guid.NewGuid(), RuleSeverity.Hard, 1, RuleShiftKind.Night, freeDays);
}
