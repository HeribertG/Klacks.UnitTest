// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for TranscriptionEnhancerService using ILLMProviderFactory and IDictionaryService.
/// </summary>
namespace Klacks.UnitTest.Services.Assistant;

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.Logging;
using SettingsConstants = Klacks.Api.Application.Constants.Settings;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;
using TranscriptionConstants = Klacks.Api.Application.Constants.TranscriptionConstants;

[TestFixture]
public class TranscriptionEnhancerServiceTests
{
    private ILLMProviderFactory _mockProviderFactory;
    private IDictionaryService _mockDictionaryService;
    private ISettingsRepository _mockSettingsRepository;
    private ILogger<TranscriptionEnhancerService> _mockLogger;
    private ILLMProvider _mockProvider;
    private TranscriptionEnhancerService _service;

    [SetUp]
    public void SetUp()
    {
        _mockProviderFactory = Substitute.For<ILLMProviderFactory>();
        _mockDictionaryService = Substitute.For<IDictionaryService>();
        _mockSettingsRepository = Substitute.For<ISettingsRepository>();
        _mockLogger = Substitute.For<ILogger<TranscriptionEnhancerService>>();
        _mockProvider = Substitute.For<ILLMProvider>();

        _mockProvider.IsEnabled.Returns(true);
        _mockDictionaryService.BuildContextAsync(Arg.Any<CancellationToken>()).Returns(string.Empty);
        _mockSettingsRepository.GetSetting(Arg.Any<string>()).Returns(Task.FromResult<SettingsModel?>(null));

        _service = new TranscriptionEnhancerService(
            _mockProviderFactory,
            _mockDictionaryService,
            _mockSettingsRepository,
            _mockLogger);
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_WhenProviderNotFound_ReturnsRawText()
    {
        var rawText = "um so the the meeting was uh good";
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(Task.FromResult<ILLMProvider?>(null));

        var result = await _service.EnhanceTranscriptionAsync(rawText, "en");

        result.Should().Be(rawText);
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_WhenProviderDisabled_ReturnsRawText()
    {
        var rawText = "um so the the meeting was uh good";
        _mockProvider.IsEnabled.Returns(false);
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);

        var result = await _service.EnhanceTranscriptionAsync(rawText, "en");

        result.Should().Be(rawText);
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_WhenExceptionOccurs_ReturnsRawText()
    {
        var rawText = "um so the the meeting was uh good";
        _mockProviderFactory
            .GetProviderForModelAsync(Arg.Any<string>())
            .Returns<ILLMProvider?>(_ => throw new InvalidOperationException("Network error"));

        var result = await _service.EnhanceTranscriptionAsync(rawText, "en");

        result.Should().Be(rawText);
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_WhenProviderReturnsFailure_ReturnsRawText()
    {
        var rawText = "um so the the meeting was uh good";
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>()).Returns(new LLMProviderResponse
        {
            Success = false,
            Content = string.Empty,
            Error = "Provider error"
        });

        var result = await _service.EnhanceTranscriptionAsync(rawText, "en");

        result.Should().Be(rawText);
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_WhenProviderReturnsEmptyContent_ReturnsRawText()
    {
        var rawText = "um so the the meeting was uh good";
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>()).Returns(new LLMProviderResponse
        {
            Success = true,
            Content = "   "
        });

        var result = await _service.EnhanceTranscriptionAsync(rawText, "en");

        result.Should().Be(rawText);
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_WhenSuccessful_ReturnsEnhancedText()
    {
        var rawText = "um so the meeting was uh good";
        var enhancedText = "The meeting was good.";
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>()).Returns(new LLMProviderResponse
        {
            Success = true,
            Content = enhancedText
        });

        var result = await _service.EnhanceTranscriptionAsync(rawText, "en");

        result.Should().Be(enhancedText);
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_WhenDictionaryContextPresent_IncludesItInSystemPrompt()
    {
        var rawText = "the klax system is ready";
        var dictionaryContext = "Klacks, Klacks.Api";
        LLMProviderRequest? capturedRequest = null;

        _mockDictionaryService.BuildContextAsync(Arg.Any<CancellationToken>()).Returns(dictionaryContext);
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);
        _mockProvider
            .ProcessAsync(Arg.Do<LLMProviderRequest>(r => capturedRequest = r))
            .Returns(new LLMProviderResponse { Success = true, Content = "The Klacks system is ready." });

        await _service.EnhanceTranscriptionAsync(rawText, "en");

        capturedRequest.Should().NotBeNull();
        capturedRequest!.SystemPrompt.Should().Contain(dictionaryContext);
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_UsesModelIdFromSettings()
    {
        var rawText = "some text";
        var customModelId = "claude-haiku-4-5";
        var setting = new SettingsModel { Type = SettingsConstants.ASSISTANT_TRANSCRIPTION_MODEL, Value = customModelId };

        _mockSettingsRepository.GetSetting(SettingsConstants.ASSISTANT_TRANSCRIPTION_MODEL)
            .Returns(Task.FromResult<SettingsModel?>(setting));
        _mockProviderFactory.GetProviderForModelAsync(customModelId).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>()).Returns(new LLMProviderResponse
        {
            Success = true,
            Content = "Some text."
        });

        await _service.EnhanceTranscriptionAsync(rawText, "en");

        await _mockProviderFactory.Received(1).GetProviderForModelAsync(customModelId);
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_WhenSettingIsEmpty_FallsBackToDefaultModelId()
    {
        var rawText = "some text";
        var setting = new SettingsModel { Type = SettingsConstants.ASSISTANT_TRANSCRIPTION_MODEL, Value = string.Empty };

        _mockSettingsRepository.GetSetting(SettingsConstants.ASSISTANT_TRANSCRIPTION_MODEL)
            .Returns(Task.FromResult<SettingsModel?>(setting));
        _mockProviderFactory.GetProviderForModelAsync(TranscriptionConstants.DefaultModelId).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>()).Returns(new LLMProviderResponse
        {
            Success = true,
            Content = "Some text."
        });

        await _service.EnhanceTranscriptionAsync(rawText, "en");

        await _mockProviderFactory.Received(1).GetProviderForModelAsync(TranscriptionConstants.DefaultModelId);
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_WhenSettingIsNull_FallsBackToDefaultModelId()
    {
        var rawText = "some text";
        _mockSettingsRepository.GetSetting(Arg.Any<string>()).Returns(Task.FromResult<SettingsModel?>(null));
        _mockProviderFactory.GetProviderForModelAsync(TranscriptionConstants.DefaultModelId).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>()).Returns(new LLMProviderResponse
        {
            Success = true,
            Content = "Some text."
        });

        await _service.EnhanceTranscriptionAsync(rawText, "en");

        await _mockProviderFactory.Received(1).GetProviderForModelAsync(TranscriptionConstants.DefaultModelId);
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_PassesRawTextAsMessage()
    {
        var rawText = "ähm also das meeting war halt gut";
        LLMProviderRequest? capturedRequest = null;

        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);
        _mockProvider
            .ProcessAsync(Arg.Do<LLMProviderRequest>(r => capturedRequest = r))
            .Returns(new LLMProviderResponse { Success = true, Content = "Das Meeting war gut." });

        await _service.EnhanceTranscriptionAsync(rawText, "de");

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Message.Should().Be(rawText);
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_TrimsEnhancedText()
    {
        var rawText = "some raw text";
        var paddedText = "  Enhanced text.  ";
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>()).Returns(new LLMProviderResponse
        {
            Success = true,
            Content = paddedText
        });

        var result = await _service.EnhanceTranscriptionAsync(rawText, "en");

        result.Should().Be(paddedText.Trim());
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_ShouldUseOverrideModelId_WhenProvided()
    {
        var rawText = "some raw text";
        var overrideModelId = "override-model";
        _mockProviderFactory.GetProviderForModelAsync(overrideModelId).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>()).Returns(new LLMProviderResponse
        {
            Success = true,
            Content = "Enhanced"
        });

        var result = await _service.EnhanceTranscriptionAsync(rawText, "de", overrideModelId);

        result.Should().Be("Enhanced");
        await _mockProviderFactory.Received(1).GetProviderForModelAsync(overrideModelId);
        await _mockSettingsRepository.DidNotReceive().GetSetting(Arg.Any<string>());
    }
}
