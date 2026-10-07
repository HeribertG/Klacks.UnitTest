// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Schedules.HolisticHarmonizer;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.HolisticHarmonizer;

/// <summary>
/// The proposal call must switch thinking off: deepseek-flash otherwise spent the whole output budget on reasoning
/// and returned empty content on every Wizard 3 iteration (measured 2026-10-07, 18 006 output tokens, no answer).
/// </summary>
[TestFixture]
public class LlmPlanProposalProviderProposeTests
{
    private const string ModelId = "deepseek-flash";

    [Test]
    public async Task ProposeAsync_SwitchesThinkingOff()
    {
        LLMProviderRequest? sent = null;
        var provider = Substitute.For<ILLMProvider>();
        provider.ProcessAsync(Arg.Do<LLMProviderRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = "{\"batches\":[]}" });

        var proposals = new LlmPlanProposalProvider(Orchestrator(provider), NullLogger<LlmPlanProposalProvider>.Instance);

        await proposals.ProposeAsync(Request(), CancellationToken.None);

        sent.ShouldNotBeNull();
        sent.ThinkingBudgetTokens.ShouldBe(ThinkingBudgetConstants.Disabled);
    }

    private static LLMProviderOrchestrator Orchestrator(ILLMProvider provider)
    {
        var repository = Substitute.For<ILLMRepository>();
        repository.GetModelByIdAsync(ModelId).Returns(new LLMModel
        {
            ModelId = ModelId, ApiModelId = ModelId, ProviderId = "deepseek", IsEnabled = true, MaxTokens = 8192,
        });
        var factory = Substitute.For<ILLMProviderFactory>();
        factory.GetProviderForModelAsync(ModelId).Returns(provider);
        return new LLMProviderOrchestrator(NullLogger<LLMProviderOrchestrator>.Instance, factory, repository);
    }

    private static PlanProposalRequest Request() => new(
        ModelId: ModelId,
        PlanText: "AK: E E -",
        AgentSummary: string.Empty,
        FragmentationSummary: string.Empty,
        MaxStepsPerBatch: 3,
        Language: "en",
        IterationIndex: 0,
        PriorRejections: [],
        PlanPng: null,
        FocusedIntent: HolisticIntent.ConsolidateBlock,
        CandidateMoves: []);
}
