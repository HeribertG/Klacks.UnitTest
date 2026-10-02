// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The Holistic Harmonizer pre-flight ping proves that the model is reachable and answers. A thinking
/// model that spent the ping's output budget on reasoning (finish reason MAX_TOKENS, reasoning tokens,
/// empty or cut-off text) has proven exactly that and must not fail the run as "unexpected ping response"
/// - gemini-3.5-flash did so live with 47 thought tokens and no text. Wrong answers without that
/// signature still fail.
/// </summary>

using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Schedules.HolisticHarmonizer;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.HolisticHarmonizer;

[TestFixture]
public class PlanProposalPingEvaluatorTests
{
    [Test]
    public void PongJson_IsHealthy()
    {
        var verdict = PlanProposalPingEvaluator.Evaluate(new LLMProviderResponse { Content = "{\"ping\":\"pong\"}" });

        verdict.IsHealthy.ShouldBeTrue();
        verdict.Error.ShouldBeNull();
        verdict.OutputBudgetSpentOnThinking.ShouldBeFalse();
    }

    [Test]
    public void PongJsonInsideACodeFence_IsHealthy()
    {
        var verdict = PlanProposalPingEvaluator.Evaluate(new LLMProviderResponse { Content = "```json\n{\"ping\":\"pong\"}\n```" });

        verdict.IsHealthy.ShouldBeTrue();
    }

    [Test]
    public void EmptyTextTruncatedAfterReasoning_IsHealthyAndFlagged()
    {
        var verdict = PlanProposalPingEvaluator.Evaluate(new LLMProviderResponse
        {
            Content = string.Empty,
            OutputTruncated = true,
            ReasoningTokens = 47,
            ReasoningWithoutContent = true
        });

        verdict.IsHealthy.ShouldBeTrue();
        verdict.OutputBudgetSpentOnThinking.ShouldBeTrue();
    }

    [Test]
    public void PartialTextTruncatedAfterReasoning_IsHealthyAndFlagged()
    {
        var verdict = PlanProposalPingEvaluator.Evaluate(new LLMProviderResponse
        {
            Content = "{\"",
            OutputTruncated = true,
            ReasoningTokens = 45
        });

        verdict.IsHealthy.ShouldBeTrue();
        verdict.OutputBudgetSpentOnThinking.ShouldBeTrue();
    }

    [Test]
    public void ReasoningWithoutContentButNoTokenCount_IsHealthy()
    {
        var verdict = PlanProposalPingEvaluator.Evaluate(new LLMProviderResponse
        {
            Content = string.Empty,
            ReasoningWithoutContent = true
        });

        verdict.IsHealthy.ShouldBeTrue();
        verdict.OutputBudgetSpentOnThinking.ShouldBeTrue();
    }

    [Test]
    public void EmptyTextWithoutReasoning_Fails()
    {
        var verdict = PlanProposalPingEvaluator.Evaluate(new LLMProviderResponse { Content = string.Empty });

        verdict.IsHealthy.ShouldBeFalse();
        verdict.Error!.ShouldContain("unexpected ping response");
    }

    [Test]
    public void TruncatedWithoutReasoning_Fails()
    {
        var verdict = PlanProposalPingEvaluator.Evaluate(new LLMProviderResponse
        {
            Content = "Sure! Here is",
            OutputTruncated = true
        });

        verdict.IsHealthy.ShouldBeFalse();
    }

    [Test]
    public void ProseAnswer_FailsWithAPreview()
    {
        var verdict = PlanProposalPingEvaluator.Evaluate(new LLMProviderResponse { Content = "Hello, how can I help?" });

        verdict.IsHealthy.ShouldBeFalse();
        verdict.Error!.ShouldContain("Hello, how can I help?");
    }

    [Test]
    public void WrongJson_Fails()
    {
        var verdict = PlanProposalPingEvaluator.Evaluate(new LLMProviderResponse { Content = "{\"ping\":\"ping\"}" });

        verdict.IsHealthy.ShouldBeFalse();
    }

    [Test]
    public void ProviderError_FailsWithTheProviderMessage()
    {
        var verdict = PlanProposalPingEvaluator.Evaluate(new LLMProviderResponse { Success = false, Error = "Invalid API key" });

        verdict.IsHealthy.ShouldBeFalse();
        verdict.Error.ShouldBe("Invalid API key");
    }
}
