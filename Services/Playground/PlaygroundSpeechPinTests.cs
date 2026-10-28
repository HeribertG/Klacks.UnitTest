// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the Playground speech pin: on the Playground every TTS request runs on Edge whatever ProviderId the
/// client sends, and the LLM provider key is never used as a speech key; with the mode off the requested provider and
/// the legacy key fallback behave as before.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.Api.Presentation.Controllers.Assistant;
using Klacks.Api.Presentation.DTOs.Assistant;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SettingRow = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundSpeechPinTests
{
    private IPlaygroundMode _mode = null!;
    private ITtsProvider _edge = null!;
    private ITtsProvider _openAi = null!;
    private TtsController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mode = Substitute.For<IPlaygroundMode>();
        _edge = Substitute.For<ITtsProvider>();
        _edge.ProviderId.Returns(TtsProviderConstants.Edge);
        _edge.SynthesizeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([1, 2, 3]);
        _openAi = Substitute.For<ITtsProvider>();
        _openAi.ProviderId.Returns(TtsProviderConstants.OpenAi);
        _openAi.SynthesizeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([9, 9]);

        _controller = new TtsController(Substitute.For<ILogger<TtsController>>(), new[] { _edge, _openAi }, _mode);
    }

    [Test]
    public async Task Synthesize_ModeOn_UsesEdgeWhateverTheClientAsks()
    {
        _mode.IsActive.Returns(true);

        var result = await _controller.Synthesize(new TtsSynthesizeRequest("Hallo.", "de", TtsProviderConstants.OpenAi), CancellationToken.None);

        result.ShouldBeOfType<FileContentResult>();
        await _edge.Received(1).SynthesizeAsync("Hallo.", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _openAi.DidNotReceive().SynthesizeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Synthesize_ModeOff_UsesTheRequestedProvider()
    {
        _mode.IsActive.Returns(false);

        await _controller.Synthesize(new TtsSynthesizeRequest("Hallo.", "de", TtsProviderConstants.OpenAi), CancellationToken.None);

        await _openAi.Received(1).SynthesizeAsync("Hallo.", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _edge.DidNotReceive().SynthesizeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Test_ModeOn_RunsOnEdgeNotOnThePaidProvider()
    {
        _mode.IsActive.Returns(true);

        await _controller.TestConnection(new TtsTestRequest(TtsProviderConstants.OpenAi), CancellationToken.None);

        await _edge.Received(1).SynthesizeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _openAi.DidNotReceive().SynthesizeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Voices_ModeOn_ListsEdgeVoicesOnly()
    {
        _mode.IsActive.Returns(true);

        await _controller.GetVoices(TtsProviderConstants.OpenAi, CancellationToken.None);

        await _edge.Received(1).GetVoicesAsync(Arg.Any<CancellationToken>());
        await _openAi.DidNotReceive().GetVoicesAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Voices_ModeOff_ListsTheRequestedProviderVoices()
    {
        _mode.IsActive.Returns(false);

        await _controller.GetVoices(TtsProviderConstants.OpenAi, CancellationToken.None);

        await _openAi.Received(1).GetVoicesAsync(Arg.Any<CancellationToken>());
    }

    private TtsApiKeyResolver Resolver(out ILLMRepository llmRepository)
    {
        var settings = Substitute.For<ISettingsRepository>();
        settings.GetSetting(Arg.Any<string>()).Returns((SettingRow?)null);
        llmRepository = Substitute.For<ILLMRepository>();
        llmRepository.GetProviderByIdAsync(TtsProviderConstants.OpenAi)
            .Returns(new LLMProvider { ProviderId = TtsProviderConstants.OpenAi, ApiKey = "llm-key" });

        return new TtsApiKeyResolver(settings, Substitute.For<Klacks.Api.Domain.Interfaces.Settings.ISettingsEncryptionService>(), llmRepository, _mode);
    }

    [Test]
    public async Task KeyResolver_ModeOn_NeverFallsBackToTheLlmProviderKey()
    {
        _mode.IsActive.Returns(true);

        var key = await Resolver(out var llmRepository).ResolveAsync(TtsProviderConstants.OpenAi);

        key.ShouldBeNull();
        await llmRepository.DidNotReceive().GetProviderByIdAsync(Arg.Any<string>());
    }

    [Test]
    public async Task KeyResolver_ModeOff_StillFallsBackToTheLlmProviderKey()
    {
        _mode.IsActive.Returns(false);

        var key = await Resolver(out _).ResolveAsync(TtsProviderConstants.OpenAi);

        key.ShouldBe("llm-key");
    }
}
