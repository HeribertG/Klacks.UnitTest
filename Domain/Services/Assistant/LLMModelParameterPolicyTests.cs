// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMModelParameterPolicyTests
{
    private const string Temperature = LLMModelParameterNames.Temperature;
    private const string StreamOptions = LLMModelParameterNames.StreamOptions;

    private static bool IsSupported(
        LLMParameterDefaultFamily family,
        string apiModelId,
        string parameterName = Temperature,
        string? declaredJson = null,
        bool fallback = true) =>
        LLMModelParameterPolicy.IsSupported(
            family, apiModelId, parameterName, ModelParameterSupport.Parse(declaredJson), fallback);

    [TestCase("claude-opus-4-5")]
    [TestCase("claude-sonnet-4-5")]
    [TestCase("claude-haiku-4-5-20251001")]
    [TestCase("claude-3-opus")]
    public void Temperature_AnthropicModelStillAcceptingIt_IsSupported(string apiModelId) =>
        IsSupported(LLMParameterDefaultFamily.Anthropic, apiModelId).ShouldBeTrue();

    [TestCase("claude-sonnet-5")]
    [TestCase("claude-opus-4-7")]
    [TestCase("claude-fable-5")]
    [TestCase("some-future-claude")]
    public void Temperature_AnthropicModelThatDroppedIt_IsOmitted(string apiModelId) =>
        IsSupported(LLMParameterDefaultFamily.Anthropic, apiModelId).ShouldBeFalse();

    [TestCase("gpt-3.5-turbo")]
    [TestCase("gpt-4o")]
    [TestCase("gpt-4o-2024-08-06")]
    [TestCase("gpt-5-chat-latest")]
    [TestCase("gpt-5.2")]
    [TestCase("gpt-5.4-nano")]
    public void Temperature_OpenAiModelAcceptingIt_IsSupported(string apiModelId) =>
        IsSupported(LLMParameterDefaultFamily.OpenAiCompatible, apiModelId).ShouldBeTrue();

    [TestCase("gpt-5-nano")]
    [TestCase("gpt-5-search-api")]
    [TestCase("o3-mini")]
    [TestCase("o4-mini")]
    [TestCase("some-future-model")]
    public void Temperature_OpenAiModelRejectingIt_IsOmitted(string apiModelId) =>
        IsSupported(LLMParameterDefaultFamily.OpenAiCompatible, apiModelId).ShouldBeFalse();

    [TestCase("deepseek-v4-pro")]
    [TestCase("mistral-large-latest")]
    [TestCase("gemini-2.5-flash")]
    public void Temperature_ProviderWithoutRestriction_KeepsSendingIt(string apiModelId) =>
        IsSupported(LLMParameterDefaultFamily.Unrestricted, apiModelId).ShouldBeTrue();

    [Test]
    public void Declaration_DisablingAParameterTheDefaultAllows_Wins() =>
        IsSupported(LLMParameterDefaultFamily.OpenAiCompatible, "gpt-4o", declaredJson: "{\"temperature\": false}")
            .ShouldBeFalse();

    [Test]
    public void Declaration_EnablingAParameterTheDefaultOmits_Wins() =>
        IsSupported(LLMParameterDefaultFamily.OpenAiCompatible, "gpt-5-nano", declaredJson: "{\"temperature\": true}")
            .ShouldBeTrue();

    [Test]
    public void Declaration_ForAnotherParameter_LeavesTemperatureOnTheDefaultRule() =>
        IsSupported(LLMParameterDefaultFamily.OpenAiCompatible, "gpt-5-nano", declaredJson: "{\"stream_options\": true}")
            .ShouldBeFalse();

    [TestCase("not json at all")]
    [TestCase("{\"temperature\": \"yes\"}")]
    [TestCase("{}")]
    [TestCase("")]
    public void Declaration_Unusable_FallsBackToTheDefaultRuleInsteadOfThrowing(string declaredJson) =>
        IsSupported(LLMParameterDefaultFamily.OpenAiCompatible, "gpt-4o", declaredJson: declaredJson)
            .ShouldBeTrue();

    [Test]
    public void Declaration_IsCaseInsensitive() =>
        IsSupported(LLMParameterDefaultFamily.OpenAiCompatible, "gpt-4o", declaredJson: "{\"Temperature\": false}")
            .ShouldBeFalse();

    [TestCase(true)]
    [TestCase(false)]
    public void ParameterWithoutBuiltInRule_UsesCallerFallback(bool fallback) =>
        IsSupported(LLMParameterDefaultFamily.OpenAiCompatible, "gpt-4o", StreamOptions, fallback: fallback)
            .ShouldBe(fallback);

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("{}")]
    [TestCase("{\"temperature\": false}")]
    [TestCase("{\"temperature\": false, \"stream_options\": true}")]
    public void TryValidate_AcceptableOperatorInput_Passes(string? json)
    {
        ModelParameterSupport.TryValidate(json, out var error).ShouldBeTrue();
        error.ShouldBeNull();
    }

    [TestCase("not json at all")]
    [TestCase("{\"temperature\": \"false\"}")]
    [TestCase("[\"temperature\"]")]
    [TestCase("{\"temperature\": 0}")]
    public void TryValidate_UnusableOperatorInput_FailsWithReason(string json)
    {
        ModelParameterSupport.TryValidate(json, out var error).ShouldBeFalse();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void ParameterWithoutBuiltInRule_DeclarationBeatsFallback() =>
        IsSupported(
            LLMParameterDefaultFamily.OpenAiCompatible,
            "gpt-4o",
            StreamOptions,
            declaredJson: "{\"stream_options\": true}",
            fallback: false).ShouldBeTrue();
}
