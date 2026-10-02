// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The pre-flight ping and the vision capability check ask for thinking to be switched off and leave
/// enough output headroom for models that think anyway, so a thinking model cannot burn the whole answer
/// budget on reasoning (gemini-3.5-flash used 47 of 50 tokens for thoughts before this fix).
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Services.Schedules.HolisticHarmonizer;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.HolisticHarmonizer;

[TestFixture]
public class HolisticHarmonizerProbeRequestsTests
{
    private static readonly LLMModel Model = new()
    {
        ModelId = "gemini-25-flash",
        ApiModelId = "gemini-2.5-flash",
        SupportedParameters = "temperature",
        CostPerInputToken = 0.0003m,
        CostPerOutputToken = 0.0025m
    };

    [Test]
    public void Ping_DisablesThinkingAndLeavesThinkingHeadroom()
    {
        var request = HolisticHarmonizerProbeRequests.Ping(Model);

        request.ThinkingBudgetTokens.ShouldBe(ThinkingBudgetConstants.Disabled);
        request.MaxTokens.ShouldBe(ModelProbeConstants.ThinkingHeadroomMaxTokens);
        request.ModelId.ShouldBe(Model.ApiModelId);
        request.AvailableFunctions.ShouldBeEmpty();
        request.Stream.ShouldBeFalse();
        request.ImagePng.ShouldBeNull();
    }

    [Test]
    public void Capability_DisablesThinkingLeavesHeadroomAndCarriesTheImage()
    {
        var png = new byte[] { 1, 2, 3 };

        var request = HolisticHarmonizerProbeRequests.Capability(Model, png);

        request.ThinkingBudgetTokens.ShouldBe(ThinkingBudgetConstants.Disabled);
        request.MaxTokens.ShouldBe(ModelProbeConstants.ThinkingHeadroomMaxTokens);
        request.ImagePng.ShouldBeSameAs(png);
        request.ModelId.ShouldBe(Model.ApiModelId);
    }

    [Test]
    public void ThinkingHeadroom_IsFarAboveTheObservedPingThinking()
    {
        const int ObservedPingThoughtTokens = 47;

        ModelProbeConstants.ThinkingHeadroomMaxTokens.ShouldBeGreaterThan(ObservedPingThoughtTokens * 10);
    }
}
