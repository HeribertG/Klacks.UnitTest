// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the chat-model pin inside LLMProviderOrchestrator: on the Playground the client ModelId and the
/// database default are ignored, a missing or disabled pinned model fails closed with a clear English message and
/// never falls back to another model; with the pin off or absent the resolution is unchanged.
/// </summary>

using Klacks.Api.Application.Services.Playground;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Playground;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundOrchestratorPinTests
{
    private const string Pinned = PlaygroundHardeningConstants.DefaultChatModelId;
    private const string Expensive = "deepseek-v4-pro";

    private ILLMRepository _repository = null!;
    private ILLMProviderFactory _factory = null!;
    private IPlaygroundMode _mode = null!;
    private LLMModel _pinnedModel = null!;
    private LLMModel _expensiveModel = null!;

    [SetUp]
    public void SetUp()
    {
        _pinnedModel = new LLMModel { ModelId = Pinned, IsEnabled = true, ProviderId = "deepseek" };
        _expensiveModel = new LLMModel { ModelId = Expensive, IsEnabled = true, ProviderId = "deepseek", IsDefault = true };

        _repository = Substitute.For<ILLMRepository>();
        _repository.GetModelByIdAsync(Pinned).Returns(_pinnedModel);
        _repository.GetModelByIdAsync(Expensive).Returns(_expensiveModel);
        _repository.GetDefaultModelAsync().Returns(_expensiveModel);

        _factory = Substitute.For<ILLMProviderFactory>();
        _factory.GetProviderForModelAsync(Arg.Any<string>()).Returns(Substitute.For<ILLMProvider>());

        _mode = Substitute.For<IPlaygroundMode>();
    }

    private LLMProviderOrchestrator Orchestrator() => new(
        Substitute.For<ILogger<LLMProviderOrchestrator>>(),
        _factory,
        _repository,
        new PlaygroundModelPin(_mode, Options.Create(new PlaygroundModelOptions())));

    [Test]
    public async Task ModeOn_ClientModelIdIsIgnored()
    {
        _mode.IsActive.Returns(true);

        var (model, provider, error) = await Orchestrator().GetModelAndProviderAsync(Expensive);

        error.ShouldBeNull();
        provider.ShouldNotBeNull();
        model.ShouldBeSameAs(_pinnedModel);
        await _repository.DidNotReceive().GetModelByIdAsync(Expensive);
        await _factory.DidNotReceive().GetProviderForModelAsync(Expensive);
    }

    [Test]
    public async Task ModeOn_NoClientModelDoesNotUseTheDatabaseDefault()
    {
        _mode.IsActive.Returns(true);

        var (model, _, error) = await Orchestrator().GetModelAndProviderAsync(null);

        error.ShouldBeNull();
        model.ShouldBeSameAs(_pinnedModel);
        await _repository.DidNotReceive().GetDefaultModelAsync();
    }

    [Test]
    public async Task ModeOn_MissingPinnedModelFailsClosedWithoutFallback()
    {
        _mode.IsActive.Returns(true);
        _repository.GetModelByIdAsync(Pinned).Returns((LLMModel?)null);

        var (model, provider, error) = await Orchestrator().GetModelAndProviderAsync(Expensive);

        model.ShouldBeNull();
        provider.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain(Pinned);
        error.ShouldContain("not available or not enabled");
        await _repository.DidNotReceive().GetDefaultModelAsync();
        await _factory.DidNotReceive().GetProviderForModelAsync(Arg.Any<string>());
    }

    [Test]
    public async Task ModeOn_DisabledPinnedModelFailsClosed()
    {
        _mode.IsActive.Returns(true);
        _pinnedModel.IsEnabled = false;

        var (model, _, error) = await Orchestrator().GetModelAndProviderAsync(null);

        model.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain(Pinned);
        await _factory.DidNotReceive().GetProviderForModelAsync(Arg.Any<string>());
    }

    [Test]
    public async Task ModeOff_ClientModelIsHonouredAsBefore()
    {
        _mode.IsActive.Returns(false);

        var (model, _, error) = await Orchestrator().GetModelAndProviderAsync(Expensive);

        error.ShouldBeNull();
        model.ShouldBeSameAs(_expensiveModel);
        await _repository.DidNotReceive().GetModelByIdAsync(Pinned);
    }

    [Test]
    public async Task ModeOff_NoClientModelUsesTheDatabaseDefault()
    {
        _mode.IsActive.Returns(false);

        var (model, _, _) = await Orchestrator().GetModelAndProviderAsync(null);

        model.ShouldBeSameAs(_expensiveModel);
    }

    [Test]
    public async Task ModeOff_UnknownModelKeepsTheOriginalMessage()
    {
        _mode.IsActive.Returns(false);

        var (_, _, error) = await Orchestrator().GetModelAndProviderAsync("does-not-exist");

        error.ShouldBe("The selected model is not available.");
    }
}
