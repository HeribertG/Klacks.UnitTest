// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ToolCallLoopSteps.ApplyRecipeInjections, the recipe-forcing data flow both chat loops run
/// before a forced step executes: the values the plan captured are written into the parameters of the
/// matching call, overriding what the model sent, and a turn without a forcing plan is left untouched.
/// </summary>

using Klacks.Api.Domain.Services.Assistant.ChatTurn;
using Klacks.Api.Domain.Services.Assistant.Providers;
using NSubstitute;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class ToolCallLoopStepsTests
{
    private const string ForcedSkill = "create_shift";
    private const string OtherSkill = "list_groups";
    private const string ClientIdParam = "clientId";
    private const string CapturedClientId = "11111111-1111-1111-1111-111111111111";
    private const string ModelClientId = "model-guess";

    [Test]
    public void ApplyRecipeInjections_WithoutAPlan_LeavesTheCallsUntouched()
    {
        var call = Call(ForcedSkill, ModelClientId);

        ToolCallLoopSteps.ApplyRecipeInjections(null, new[] { call });

        call.Parameters[ClientIdParam].ShouldBe(ModelClientId);
    }

    [Test]
    public void ApplyRecipeInjections_OverridesTheParameterTheModelSent()
    {
        var call = Call(ForcedSkill, ModelClientId);

        ToolCallLoopSteps.ApplyRecipeInjections(PlanInjectingClientId(), new[] { call });

        call.Parameters[ClientIdParam].ShouldBe(CapturedClientId);
    }

    [Test]
    public void ApplyRecipeInjections_AddsAParameterTheModelLeftOut()
    {
        var call = new LLMFunctionCall { FunctionName = ForcedSkill, Parameters = new Dictionary<string, object>() };

        ToolCallLoopSteps.ApplyRecipeInjections(PlanInjectingClientId(), new[] { call });

        call.Parameters[ClientIdParam].ShouldBe(CapturedClientId);
    }

    [Test]
    public void ApplyRecipeInjections_LeavesCallsOfOtherSkillsUntouched()
    {
        var call = Call(OtherSkill, ModelClientId);

        ToolCallLoopSteps.ApplyRecipeInjections(PlanInjectingClientId(), new[] { call });

        call.Parameters[ClientIdParam].ShouldBe(ModelClientId);
    }

    private static IRecipeForcingPlan PlanInjectingClientId()
    {
        var plan = Substitute.For<IRecipeForcingPlan>();
        plan.GetParameterInjections(Arg.Any<string>()).Returns(new Dictionary<string, object>());
        plan.GetParameterInjections(ForcedSkill)
            .Returns(new Dictionary<string, object> { [ClientIdParam] = CapturedClientId });
        return plan;
    }

    private static LLMFunctionCall Call(string skill, string clientId) => new()
    {
        FunctionName = skill,
        Parameters = new Dictionary<string, object> { [ClientIdParam] = clientId }
    };
}
