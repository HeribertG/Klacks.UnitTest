// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/**
 * Unit tests for SpeechModelCheckService covering empty model lists, provider unavailability,
 * successful ping responses, and unexpected reply content.
 */
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class SpeechModelCheckServiceTests
{
    private ILLMRepository _repo = null!;
    private ILLMProviderFactory _factory = null!;
    private LLMProviderOrchestrator _orchestrator = null!;
    private SpeechModelCheckService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = Substitute.For<ILLMRepository>();
        _factory = Substitute.For<ILLMProviderFactory>();
        _orchestrator = new LLMProviderOrchestrator(
            NullLogger<LLMProviderOrchestrator>.Instance,
            _factory,
            _repo);
        _sut = new SpeechModelCheckService(_repo, _orchestrator, NullLogger<SpeechModelCheckService>.Instance);
    }

    [Test]
    public async Task CheckAllAsync_NoEnabledModels_ReturnsEmptyList()
    {
        _repo.GetModelsAsync(true).Returns(new List<LLMModel>());

        var result = await _sut.CheckAllAsync(CancellationToken.None);

        result.Count.ShouldBe(0);
    }

    [Test]
    public async Task CheckAllAsync_ProviderUnavailable_ReturnsUnhealthyWithMetadata()
    {
        var model = BuildModel("groq-llama-fast", "Groq Llama Fast", "groq", 128000, 0m, 0m);
        _repo.GetModelsAsync(true).Returns(new List<LLMModel> { model });
        _repo.GetModelByIdAsync(model.ModelId).Returns(model);
        _factory.GetProviderForModelAsync(model.ModelId).Returns((ILLMProvider?)null);

        var result = await _sut.CheckAllAsync(CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].IsHealthy.ShouldBeFalse();
        result[0].ContextWindow.ShouldBe(128000);
        result[0].CostPerInputToken.ShouldBe(0m);
        result[0].CostPerOutputToken.ShouldBe(0m);
        result[0].Error.ShouldNotBeNull();
    }

    [Test]
    public async Task CheckAllAsync_HealthyPing_ReturnsHealthyResult()
    {
        var model = BuildModel("haiku-fast", "Claude Haiku", "anthropic", 200000, 0.0008m, 0.004m);
        _repo.GetModelsAsync(true).Returns(new List<LLMModel> { model });
        _repo.GetModelByIdAsync(model.ModelId).Returns(model);

        var provider = Substitute.For<ILLMProvider>();
        provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new LLMProviderResponse { Success = true, Content = "ok" }));
        _factory.GetProviderForModelAsync(model.ModelId).Returns(provider);

        var result = await _sut.CheckAllAsync(CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].IsHealthy.ShouldBeTrue();
        result[0].Error.ShouldBeNull();
        result[0].DisplayName.ShouldBe("Claude Haiku");
    }

    [Test]
    public async Task CheckAllAsync_UnexpectedContent_ReturnsUnhealthyWithPreview()
    {
        var model = BuildModel("noisy-model", "Noisy Model", "test", 32000, 0m, 0m);
        _repo.GetModelsAsync(true).Returns(new List<LLMModel> { model });
        _repo.GetModelByIdAsync(model.ModelId).Returns(model);

        var provider = Substitute.For<ILLMProvider>();
        provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new LLMProviderResponse
                {
                    Success = true,
                    Content = "I cannot reply with a single word; here is a longer explanation instead.",
                }));
        _factory.GetProviderForModelAsync(model.ModelId).Returns(provider);

        var result = await _sut.CheckAllAsync(CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].IsHealthy.ShouldBeFalse();
        result[0].Error.ShouldNotBeNull();
        result[0].Error!.ShouldContain("unexpected");
    }

    private static LLMModel BuildModel(
        string modelId,
        string modelName,
        string providerId,
        int contextWindow,
        decimal costInput,
        decimal costOutput) => new()
        {
            ModelId = modelId,
            ApiModelId = modelId,
            ProviderId = providerId,
            ModelName = modelName,
            ContextWindow = contextWindow,
            MaxTokens = 4096,
            CostPerInputToken = costInput,
            CostPerOutputToken = costOutput,
            IsEnabled = true,
        };
}
