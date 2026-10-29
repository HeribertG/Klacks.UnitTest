// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ToolCallLoopSteps.ApplyRecipeInjections, the recipe-forcing data flow both chat loops run
/// before a forced step executes: the values the plan captured are written into the parameters of the
/// matching call, overriding what the model sent, and a turn without a forcing plan is left untouched. An enum
/// parameter of the toolset gets the allowed value in its declared spelling, and a raw answer that is no allowed
/// value gives way to the model's own value.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.ChatTurn;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.Logging.Abstractions;
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
    private const string BundleSkill = "bundle_nearby_timerange_shifts_into_container";
    private const string TransportModeParam = "transportMode";
    private const string RawAnswer = "Mit dem Auto.";
    private const string Car = "car";

    private static readonly IReadOnlyList<LLMFunction> NoFunctions = [];

    [Test]
    public void ApplyRecipeInjections_WithoutAPlan_LeavesTheCallsUntouched()
    {
        var call = Call(ForcedSkill, ModelClientId);

        ToolCallLoopSteps.ApplyRecipeInjections(null, new[] { call }, NoFunctions, NullLogger.Instance);

        call.Parameters[ClientIdParam].ShouldBe(ModelClientId);
    }

    [Test]
    public void ApplyRecipeInjections_OverridesTheParameterTheModelSent()
    {
        var call = Call(ForcedSkill, ModelClientId);

        ToolCallLoopSteps.ApplyRecipeInjections(PlanInjectingClientId(), new[] { call }, NoFunctions, NullLogger.Instance);

        call.Parameters[ClientIdParam].ShouldBe(CapturedClientId);
    }

    [Test]
    public void ApplyRecipeInjections_AddsAParameterTheModelLeftOut()
    {
        var call = new LLMFunctionCall { FunctionName = ForcedSkill, Parameters = new Dictionary<string, object>() };

        ToolCallLoopSteps.ApplyRecipeInjections(PlanInjectingClientId(), new[] { call }, NoFunctions, NullLogger.Instance);

        call.Parameters[ClientIdParam].ShouldBe(CapturedClientId);
    }

    [Test]
    public void ApplyRecipeInjections_LeavesCallsOfOtherSkillsUntouched()
    {
        var call = Call(OtherSkill, ModelClientId);

        ToolCallLoopSteps.ApplyRecipeInjections(PlanInjectingClientId(), new[] { call }, NoFunctions, NullLogger.Instance);

        call.Parameters[ClientIdParam].ShouldBe(ModelClientId);
    }

    [Test]
    public void ApplyRecipeInjections_EnumSlotHoldingARawAnswer_KeepsTheModelsMappedValue()
    {
        var call = BundleCall(Car);

        ToolCallLoopSteps.ApplyRecipeInjections(PlanInjectingTransportMode(RawAnswer), new[] { call }, BundleToolset(), NullLogger.Instance);

        call.Parameters[TransportModeParam].ShouldBe(Car);
    }

    [Test]
    public void ApplyRecipeInjections_EnumSlotHoldingARawAnswer_KeepsAModelValueThatArrivedAsJson()
    {
        var modelValue = JsonDocument.Parse("\"car\"").RootElement.Clone();
        var call = new LLMFunctionCall
        {
            FunctionName = BundleSkill,
            Parameters = new Dictionary<string, object> { [TransportModeParam] = modelValue }
        };

        ToolCallLoopSteps.ApplyRecipeInjections(PlanInjectingTransportMode(RawAnswer), new[] { call }, BundleToolset(), NullLogger.Instance);

        call.Parameters[TransportModeParam].ShouldBe(modelValue);
    }

    [Test]
    public void ApplyRecipeInjections_EnumSlotHoldingARawAnswer_WithoutAModelValue_InjectsTheAnswerSoValidationFailsLoudly()
    {
        var call = new LLMFunctionCall { FunctionName = BundleSkill, Parameters = new Dictionary<string, object>() };

        ToolCallLoopSteps.ApplyRecipeInjections(PlanInjectingTransportMode(RawAnswer), new[] { call }, BundleToolset(), NullLogger.Instance);

        call.Parameters[TransportModeParam].ShouldBe(RawAnswer);
    }

    [Test]
    public void ApplyRecipeInjections_EnumSlotHoldingAnAllowedValue_InjectsItInTheDeclaredSpelling()
    {
        var call = BundleCall("foot");

        ToolCallLoopSteps.ApplyRecipeInjections(PlanInjectingTransportMode(" CAR "), new[] { call }, BundleToolset(), NullLogger.Instance);

        call.Parameters[TransportModeParam].ShouldBe(Car);
    }

    [Test]
    public void ApplyRecipeInjections_SkillNotInTheToolset_KeepsThePlainOverride()
    {
        var call = BundleCall(Car);

        ToolCallLoopSteps.ApplyRecipeInjections(PlanInjectingTransportMode(RawAnswer), new[] { call }, NoFunctions, NullLogger.Instance);

        call.Parameters[TransportModeParam].ShouldBe(RawAnswer);
    }

    private static IRecipeForcingPlan PlanInjectingTransportMode(string slotValue)
    {
        var plan = Substitute.For<IRecipeForcingPlan>();
        plan.GetParameterInjections(Arg.Any<string>()).Returns(new Dictionary<string, object>());
        plan.GetParameterInjections(BundleSkill)
            .Returns(new Dictionary<string, object> { [TransportModeParam] = slotValue });
        return plan;
    }

    private static IReadOnlyList<LLMFunction> BundleToolset() =>
    [
        new LLMFunction
        {
            Name = BundleSkill,
            Parameters = new Dictionary<string, object>
            {
                [TransportModeParam] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    [SkillParameterSchemaKeys.Enum] = new List<string> { Car, "bicycle", "foot", "mix" }
                }
            }
        }
    ];

    private static LLMFunctionCall BundleCall(string transportMode) => new()
    {
        FunctionName = BundleSkill,
        Parameters = new Dictionary<string, object> { [TransportModeParam] = transportMode }
    };

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
