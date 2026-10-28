// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the transcription pin in TranscriptionEnhancerService: on the Playground the cleanup runs on the
/// pinned provider and model whatever the client sends, resolves nothing through the model table, and an
/// unavailable pinned provider returns the unenhanced text without trying any other model; with the pin off the
/// requested or configured model is resolved as before.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Domain.Models.Playground;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.Logging;
using LLMModel = Klacks.Api.Domain.Models.Assistant.LLMModel;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;
using TranscriptionConstants = Klacks.Api.Application.Constants.TranscriptionConstants;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundTranscriptionPinTests
{
    private const string RawText = "wir brauchen 2 mitarbeiter fuer morgen";
    private const string Cleaned = "Wir brauchen 2 Mitarbeiter für morgen.";

    private ILLMProviderFactory _factory = null!;
    private ILLMRepository _llmRepository = null!;
    private IPlaygroundModelPin _pin = null!;
    private ILLMProvider _provider = null!;
    private TranscriptionEnhancerService _sut = null!;
    private LLMProviderRequest? _captured;

    [SetUp]
    public void SetUp()
    {
        _factory = Substitute.For<ILLMProviderFactory>();
        _llmRepository = Substitute.For<ILLMRepository>();
        _pin = Substitute.For<IPlaygroundModelPin>();
        _provider = Substitute.For<ILLMProvider>();
        _provider.IsEnabled.Returns(true);
        _captured = null;
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _captured = call.Arg<LLMProviderRequest>();
                return Task.FromResult(new LLMProviderResponse { Success = true, Content = Cleaned });
            });

        var dictionary = Substitute.For<IDictionaryService>();
        dictionary.ApplyReplacementsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult((string)call[0]));
        dictionary.BuildContextAsync(Arg.Any<CancellationToken>()).Returns(string.Empty);

        var settings = Substitute.For<ISettingsRepository>();
        settings.GetSetting(Arg.Any<string>()).Returns(Task.FromResult<SettingsModel?>(null));

        _sut = new TranscriptionEnhancerService(
            _factory, dictionary, settings, _llmRepository, _pin, Substitute.For<ILogger<TranscriptionEnhancerService>>());
    }

    [Test]
    public async Task Pinned_UsesPinnedProviderAndModelAndIgnoresTheClientModel()
    {
        _pin.GetTranscriptionTarget().Returns(new PlaygroundTranscriptionTarget("groq", "qwen/qwen3.8-27b"));
        _factory.GetProviderAsync("groq").Returns(_provider);

        var result = await _sut.EnhanceTranscriptionAsync(RawText, "de", "deepseek-v4-pro");

        result.ShouldBe(Cleaned);
        _captured.ShouldNotBeNull();
        _captured.ModelId.ShouldBe("qwen/qwen3.8-27b");
        await _factory.DidNotReceive().GetProviderForModelAsync(Arg.Any<string>());
        await _llmRepository.DidNotReceive().GetModelByIdAsync(Arg.Any<string>());
    }

    [Test]
    public async Task Pinned_MissingProviderReturnsPreprocessedTextAndTriesNothingElse()
    {
        _pin.GetTranscriptionTarget().Returns(new PlaygroundTranscriptionTarget("groq", "qwen/qwen3.8-27b"));
        _factory.GetProviderAsync("groq").Returns((ILLMProvider?)null);

        var result = await _sut.EnhanceTranscriptionAsync(RawText, "de", "deepseek-v4-pro");

        result.ShouldBe(RawText);
        await _provider.DidNotReceive().ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>());
        await _factory.DidNotReceive().GetProviderForModelAsync(Arg.Any<string>());
    }

    [Test]
    public async Task Pinned_DisabledProviderReturnsPreprocessedText()
    {
        _pin.GetTranscriptionTarget().Returns(new PlaygroundTranscriptionTarget("groq", "qwen/qwen3.8-27b"));
        var disabled = Substitute.For<ILLMProvider>();
        disabled.IsEnabled.Returns(false);
        _factory.GetProviderAsync("groq").Returns(disabled);

        var result = await _sut.EnhanceTranscriptionAsync(RawText, "de", null);

        result.ShouldBe(RawText);
        await disabled.DidNotReceive().ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NotPinned_ResolvesTheRequestedModelAsBefore()
    {
        _pin.GetTranscriptionTarget().Returns((PlaygroundTranscriptionTarget?)null);
        _factory.GetProviderForModelAsync("client-model").Returns(_provider);
        _llmRepository.GetModelByIdAsync("client-model").Returns(new LLMModel { ModelId = "client-model", ApiModelId = "client-api" });

        var result = await _sut.EnhanceTranscriptionAsync(RawText, "de", "client-model");

        result.ShouldBe(Cleaned);
        _captured.ShouldNotBeNull();
        _captured.ModelId.ShouldBe("client-api");
        await _factory.DidNotReceive().GetProviderAsync(Arg.Any<string>());
    }

    [Test]
    public async Task NotPinned_WithoutRequestedModelUsesTheConfiguredDefault()
    {
        _pin.GetTranscriptionTarget().Returns((PlaygroundTranscriptionTarget?)null);
        _factory.GetProviderForModelAsync(TranscriptionConstants.DefaultModelId).Returns(_provider);

        await _sut.EnhanceTranscriptionAsync(RawText, "de", null);

        await _factory.Received(1).GetProviderForModelAsync(TranscriptionConstants.DefaultModelId);
    }
}
