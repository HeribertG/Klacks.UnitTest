// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the Playground model pin and its options: with the mode off the caller choice passes through and
/// no transcription target exists; with the mode on the configured chat model replaces whatever the client sent;
/// blank configuration values keep the measured defaults.
/// </summary>

using Klacks.Api.Application.Services.Playground;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Domain.Services.Playground;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundModelPinTests
{
    private IPlaygroundMode _mode = null!;

    [SetUp]
    public void SetUp()
    {
        _mode = Substitute.For<IPlaygroundMode>();
    }

    private PlaygroundModelPin Pin(PlaygroundModelOptions? options = null) =>
        new(_mode, Options.Create(options ?? new PlaygroundModelOptions()));

    [Test]
    public void ModeOff_PassesTheRequestedModelThrough()
    {
        _mode.IsActive.Returns(false);
        var sut = Pin();

        sut.IsPinned.ShouldBeFalse();
        sut.ResolveChatModelId("client-model").ShouldBe("client-model");
        sut.ResolveChatModelId(null).ShouldBeNull();
        sut.GetTranscriptionTarget().ShouldBeNull();
    }

    [Test]
    public void ModeOn_IgnoresTheClientModelAndReturnsThePinnedOne()
    {
        _mode.IsActive.Returns(true);
        var sut = Pin();

        sut.IsPinned.ShouldBeTrue();
        sut.ResolveChatModelId("deepseek-v4-pro").ShouldBe(PlaygroundHardeningConstants.DefaultChatModelId);
        sut.ResolveChatModelId(null).ShouldBe(PlaygroundHardeningConstants.DefaultChatModelId);
    }

    [Test]
    public void ModeOn_TranscriptionTargetIsTheConfiguredGroqModel()
    {
        _mode.IsActive.Returns(true);

        var target = Pin().GetTranscriptionTarget();

        target.ShouldNotBeNull();
        target.ProviderId.ShouldBe(PlaygroundHardeningConstants.DefaultTranscriptionProviderId);
        target.ApiModelId.ShouldBe(PlaygroundHardeningConstants.DefaultTranscriptionApiModelId);
    }

    [Test]
    public void Defaults_AreDeepseekFlashAndQwenOnGroq()
    {
        PlaygroundHardeningConstants.DefaultChatModelId.ShouldBe("deepseek-flash");
        PlaygroundHardeningConstants.DefaultTranscriptionProviderId.ShouldBe("groq");
        PlaygroundHardeningConstants.DefaultTranscriptionApiModelId.ShouldBe("qwen/qwen3.8-27b");
    }

    [Test]
    public void Options_ConfigurationOverridesAndBlankKeepsTheDefault()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ChatModelId"] = "  other-chat  ",
                ["TranscriptionProviderId"] = "   ",
            })
            .Build();
        var options = new PlaygroundModelOptions();

        options.ApplyConfiguration(configuration);

        options.ChatModelId.ShouldBe("other-chat");
        options.TranscriptionProviderId.ShouldBe(PlaygroundHardeningConstants.DefaultTranscriptionProviderId);
        options.TranscriptionApiModelId.ShouldBe(PlaygroundHardeningConstants.DefaultTranscriptionApiModelId);
    }

    [Test]
    public void ModeOn_ConfiguredChatModelWins()
    {
        _mode.IsActive.Returns(true);

        Pin(new PlaygroundModelOptions { ChatModelId = "configured" }).ResolveChatModelId("client").ShouldBe("configured");
    }
}
