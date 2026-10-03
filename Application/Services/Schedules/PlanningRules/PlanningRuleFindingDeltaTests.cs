// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for PlanningRuleFindingDelta: grouping per (rule, agent), total-excess gate, same-date domination and
/// the documented blind spot (repair one, create another of equal excess).
/// </summary>

using Klacks.Api.Application.Services.Schedules.PlanningRules;
using Klacks.ScheduleOptimizer.Constraints.Rules;

namespace Klacks.UnitTest.Application.Services.Schedules.PlanningRules;

[TestFixture]
public class PlanningRuleFindingDeltaTests
{
    private static readonly DateOnly Day = new(2026, 7, 13);
    private static readonly Guid RuleA = Guid.NewGuid();
    private static readonly Guid RuleB = Guid.NewGuid();

    [Test]
    public void SameFindingsBeforeAndAfter_ReportNothing()
    {
        var findings = new[] { Finding(RuleA, "a", Day, 2m) };

        PlanningRuleFindingDelta.NewOrWorsened(findings, findings).ShouldBeEmpty();
    }

    [Test]
    public void NewFinding_IsReported()
    {
        var created = Finding(RuleA, "a", Day, 1m);

        PlanningRuleFindingDelta.NewOrWorsened([], [created]).ShouldHaveSingleItem().ShouldBe(created);
    }

    [Test]
    public void LargerExcessOnTheSameDate_IsReported()
    {
        var worsened = Finding(RuleA, "a", Day, 2m);

        PlanningRuleFindingDelta.NewOrWorsened([Finding(RuleA, "a", Day, 1m)], [worsened]).ShouldHaveSingleItem().ShouldBe(worsened);
    }

    [Test]
    public void AnchorShiftWithLowerExcess_IsNotReported()
    {
        PlanningRuleFindingDelta.NewOrWorsened([Finding(RuleA, "a", Day, 2m)], [Finding(RuleA, "a", Day.AddDays(1), 1m)]).ShouldBeEmpty();
    }

    [Test]
    public void OtherAgentOrRule_DoesNotMaskAFinding()
    {
        var before = new[] { Finding(RuleA, "b", Day, 3m), Finding(RuleB, "a", Day, 3m) };
        var created = Finding(RuleA, "a", Day, 1m);

        PlanningRuleFindingDelta.NewOrWorsened(before, [.. before, created]).ShouldHaveSingleItem().ShouldBe(created);
    }

    [Test]
    public void RisingGroup_KeepsUnchangedFindingsOut()
    {
        var unchanged = Finding(RuleA, "a", Day, 1m);
        var created = Finding(RuleA, "a", Day.AddDays(5), 1m);

        PlanningRuleFindingDelta.NewOrWorsened([unchanged], [unchanged, created]).ShouldHaveSingleItem().ShouldBe(created);
    }

    [Test]
    public void RepairOneCreateAnotherOfEqualExcess_IsTheDocumentedBlindSpot()
    {
        PlanningRuleFindingDelta.NewOrWorsened([Finding(RuleA, "a", Day, 1m)], [Finding(RuleA, "a", Day.AddDays(9), 1m)]).ShouldBeEmpty();
    }

    private static RuleFinding Finding(Guid ruleId, string agentId, DateOnly date, decimal excess)
        => new(ruleId, PlanRuleKind.MaxConsecutiveOfKind, RuleSeverity.Hard, agentId, date, 5m + excess, 5m, excess);
}
