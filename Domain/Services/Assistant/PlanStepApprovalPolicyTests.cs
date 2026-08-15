// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Full truth-table tests for PlanStepApprovalPolicy — the plan-path HITL approval decision derived
/// from a step's skill risk class and the user's autonomy level. Locks the exact behaviour, including
/// the deliberate deviation from AutonomyGateService.IsAllowed: Irreversible requires FullyAutonomous
/// here, one level stricter than the chat gate's Autonomous. Sensitive always requires approval on both
/// paths, so it is no longer a deviation.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class PlanStepApprovalPolicyTests
{
    [Test]
    public void RequiresApproval_MatchesFullTruthTable()
    {
        foreach (SkillRiskClass riskClass in Enum.GetValues<SkillRiskClass>())
        foreach (AutonomyLevel level in Enum.GetValues<AutonomyLevel>())
        {
            var actual = PlanStepApprovalPolicy.RequiresApproval(riskClass, level);
            var expected = ExpectedRequiresApproval(riskClass, level);

            actual.ShouldBe(expected, $"riskClass={riskClass} level={level}");
        }
    }

    [TestCase(SkillRiskClass.ReadOnly, AutonomyLevel.Propose, false)]
    [TestCase(SkillRiskClass.ReadOnly, AutonomyLevel.FullyAutonomous, false)]
    [TestCase(SkillRiskClass.Sensitive, AutonomyLevel.Propose, true)]
    [TestCase(SkillRiskClass.Sensitive, AutonomyLevel.FullyAutonomous, true)]
    [TestCase(SkillRiskClass.Reversible, AutonomyLevel.Propose, true)]
    [TestCase(SkillRiskClass.Reversible, AutonomyLevel.Assisted, false)]
    [TestCase(SkillRiskClass.Reversible, AutonomyLevel.FullyAutonomous, false)]
    [TestCase(SkillRiskClass.ScenarioGated, AutonomyLevel.Propose, true)]
    [TestCase(SkillRiskClass.ScenarioGated, AutonomyLevel.Assisted, false)]
    [TestCase(SkillRiskClass.Irreversible, AutonomyLevel.Autonomous, true)]
    [TestCase(SkillRiskClass.Irreversible, AutonomyLevel.FullyAutonomous, false)]
    public void RequiresApproval_RepresentativeRows(SkillRiskClass riskClass, AutonomyLevel level, bool expected)
    {
        PlanStepApprovalPolicy.RequiresApproval(riskClass, level).ShouldBe(expected);
    }

    [Test]
    public void RequiresApproval_Sensitive_AlwaysTrue_RegardlessOfLevel()
    {
        foreach (AutonomyLevel level in Enum.GetValues<AutonomyLevel>())
        {
            PlanStepApprovalPolicy.RequiresApproval(SkillRiskClass.Sensitive, level).ShouldBeTrue();
        }
    }

    [Test]
    public void RequiresApproval_Irreversible_StricterThanChatGate_NeedsFullyAutonomous()
    {
        PlanStepApprovalPolicy.RequiresApproval(SkillRiskClass.Irreversible, AutonomyLevel.Autonomous).ShouldBeTrue();
        PlanStepApprovalPolicy.RequiresApproval(SkillRiskClass.Irreversible, AutonomyLevel.FullyAutonomous).ShouldBeFalse();
    }

    [Test]
    public void RequiresApproval_UnknownRiskClass_FailsClosed()
    {
        const SkillRiskClass unknown = (SkillRiskClass)999;

        foreach (AutonomyLevel level in Enum.GetValues<AutonomyLevel>())
        {
            PlanStepApprovalPolicy.RequiresApproval(unknown, level).ShouldBeTrue();
        }
    }

    private static bool ExpectedRequiresApproval(SkillRiskClass riskClass, AutonomyLevel level) => riskClass switch
    {
        SkillRiskClass.ReadOnly => false,
        SkillRiskClass.Sensitive => true,
        SkillRiskClass.Reversible => level < AutonomyLevel.Assisted,
        SkillRiskClass.ScenarioGated => level < AutonomyLevel.Assisted,
        SkillRiskClass.Irreversible => level < AutonomyLevel.FullyAutonomous,
        _ => true
    };
}
