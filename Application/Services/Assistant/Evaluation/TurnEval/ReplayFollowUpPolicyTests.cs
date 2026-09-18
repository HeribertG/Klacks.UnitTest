// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins when a replay may ask the model a second time. Only a plain read-only lookup or an advisory
/// skill in front of an expected mutation qualifies; a navigation, a UI passthrough, a read-only
/// expectation or an unknown skill never does, because rescuing those would turn real wrong choices
/// into hits.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

[TestFixture]
public class ReplayFollowUpPolicyTests
{
    private const string LookupTool = "search_employees";
    private const string MutatingTool = "delete_client";

    private static AgentSkill Skill(
        string name, SkillEffect effect, string executionType = LlmExecutionTypes.Skill) =>
        new() { Name = name, Effect = effect, ExecutionType = executionType };

    [Test]
    public void ReadLookupBeforeExpectedMutation_Qualifies()
    {
        ReplayFollowUpPolicy.ShouldFollowUp(
            Skill(LookupTool, SkillEffect.Read), Skill(MutatingTool, SkillEffect.Mutate)).ShouldBeTrue();
    }

    [Test]
    public void AdviseLookupBeforeExpectedMutation_Qualifies()
    {
        ReplayFollowUpPolicy.ShouldFollowUp(
            Skill("evaluate_scenario", SkillEffect.Advise), Skill(MutatingTool, SkillEffect.Mutate)).ShouldBeTrue();
    }

    [Test]
    public void ExpectedToolIsReadOnly_DoesNotQualify()
    {
        ReplayFollowUpPolicy.ShouldFollowUp(
            Skill(LookupTool, SkillEffect.Read), Skill("list_groups", SkillEffect.Read)).ShouldBeFalse();
    }

    [Test]
    public void ChosenToolMutates_DoesNotQualify()
    {
        ReplayFollowUpPolicy.ShouldFollowUp(
            Skill("create_group", SkillEffect.Mutate), Skill(MutatingTool, SkillEffect.Mutate)).ShouldBeFalse();
    }

    [TestCase(SkillNames.NavigateTo)]
    [TestCase(SkillNames.SearchAndNavigate)]
    public void NavigationSkill_DoesNotQualify_EvenWhenItsEffectIsRead(string navigationSkill)
    {
        ReplayFollowUpPolicy.ShouldFollowUp(
            Skill(navigationSkill, SkillEffect.Read), Skill(MutatingTool, SkillEffect.Mutate)).ShouldBeFalse();
    }

    [TestCase(LlmExecutionTypes.UiPassthrough)]
    [TestCase(LlmExecutionTypes.UiAction)]
    public void NonSkillExecutionType_DoesNotQualify_BecauseProductionEndsTheTurnThere(string executionType)
    {
        ReplayFollowUpPolicy.ShouldFollowUp(
            Skill(LookupTool, SkillEffect.Read, executionType),
            Skill(MutatingTool, SkillEffect.Mutate)).ShouldBeFalse();
    }

    [Test]
    public void UnknownSkillOnEitherSide_DoesNotQualify()
    {
        ReplayFollowUpPolicy.ShouldFollowUp(null, Skill(MutatingTool, SkillEffect.Mutate)).ShouldBeFalse();
        ReplayFollowUpPolicy.ShouldFollowUp(Skill(LookupTool, SkillEffect.Read), null).ShouldBeFalse();
    }
}
