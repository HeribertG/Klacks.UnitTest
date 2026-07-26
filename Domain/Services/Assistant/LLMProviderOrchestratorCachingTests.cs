// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the per-request resolution cache in LLMProviderOrchestrator. A chat turn resolves
/// the model twice — once early to size the tool budget, once inside LLMService — and both must end
/// up on the identical model, which is what pins the turn to one model.
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMProviderOrchestratorCachingTests
{
    private const string ModelId = "claude-test";

    private ILLMRepository _repository = null!;
    private ILLMProviderFactory _factory = null!;
    private LLMProviderOrchestrator _orchestrator = null!;
    private LLMModel _model = null!;

    [SetUp]
    public void SetUp()
    {
        _model = new LLMModel { ModelId = ModelId, IsEnabled = true, ProviderId = "anthropic" };

        _repository = Substitute.For<ILLMRepository>();
        _repository.GetDefaultModelAsync().Returns(_model);
        _repository.GetModelByIdAsync(ModelId).Returns(_model);

        _factory = Substitute.For<ILLMProviderFactory>();
        _factory.GetProviderForModelAsync(ModelId).Returns(Substitute.For<ILLMProvider>());

        _orchestrator = new LLMProviderOrchestrator(
            Substitute.For<ILogger<LLMProviderOrchestrator>>(), _factory, _repository);
    }

    [Test]
    public async Task GetModelAndProviderAsync_CalledTwiceWithSameId_HitsTheRepositoryOnce()
    {
        await _orchestrator.GetModelAndProviderAsync(ModelId);
        await _orchestrator.GetModelAndProviderAsync(ModelId);

        await _repository.Received(1).GetModelByIdAsync(ModelId);
        await _factory.Received(1).GetProviderForModelAsync(ModelId);
    }

    [Test]
    public async Task GetModelAndProviderAsync_NullThenResolvedId_StillHitsTheRepositoryOnce()
    {
        var (firstModel, _, _) = await _orchestrator.GetModelAndProviderAsync(null);
        var (secondModel, _, _) = await _orchestrator.GetModelAndProviderAsync(firstModel!.ModelId);

        secondModel.ShouldBeSameAs(firstModel);
        await _repository.Received(1).GetModelByIdAsync(ModelId);
        await _repository.Received(1).GetDefaultModelAsync();
    }

    [Test]
    public async Task GetModelAndProviderAsync_DefaultChangesBetweenCalls_KeepsTheTurnPinnedToTheFirstModel()
    {
        var (firstModel, _, _) = await _orchestrator.GetModelAndProviderAsync(null);

        var switchedModel = new LLMModel { ModelId = "other-model", IsEnabled = true, ProviderId = "openai" };
        _repository.GetDefaultModelAsync().Returns(switchedModel);

        var (secondModel, _, error) = await _orchestrator.GetModelAndProviderAsync(null);

        error.ShouldBeNull();
        secondModel.ShouldBeSameAs(firstModel);
    }

    [Test]
    public async Task GetModelAndProviderAsync_ModelNotFound_IsNotCachedSoALaterCallCanSucceed()
    {
        _repository.GetModelByIdAsync("missing").Returns((LLMModel?)null);

        var (_, _, firstError) = await _orchestrator.GetModelAndProviderAsync("missing");
        firstError.ShouldNotBeNull();

        _repository.GetModelByIdAsync("missing").Returns(_model);
        _factory.GetProviderForModelAsync("missing").Returns(Substitute.For<ILLMProvider>());

        var (recoveredModel, _, secondError) = await _orchestrator.GetModelAndProviderAsync("missing");

        secondError.ShouldBeNull();
        recoveredModel.ShouldNotBeNull();
    }
}
