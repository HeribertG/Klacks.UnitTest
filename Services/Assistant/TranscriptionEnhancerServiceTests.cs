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
using SttConstants = Klacks.Api.Application.Constants.SttProviderConstants;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;
using LLMModel = Klacks.Api.Domain.Models.Assistant.LLMModel;
using TranscriptionConstants = Klacks.Api.Application.Constants.TranscriptionConstants;

[TestFixture]
public class TranscriptionEnhancerServiceTests
{
    private ILLMProviderFactory _mockProviderFactory;
    private IDictionaryService _mockDictionaryService;
    private ISettingsRepository _mockSettingsRepository;
    private ILLMRepository _mockLlmRepository;
    private ILogger<TranscriptionEnhancerService> _mockLogger;
    private ILLMProvider _mockProvider;
    private TranscriptionEnhancerService _service;

    [SetUp]
    public void SetUp()
    {
        _mockProviderFactory = Substitute.For<ILLMProviderFactory>();
        _mockDictionaryService = Substitute.For<IDictionaryService>();
        _mockSettingsRepository = Substitute.For<ISettingsRepository>();
        _mockLlmRepository = Substitute.For<ILLMRepository>();
        _mockLogger = Substitute.For<ILogger<TranscriptionEnhancerService>>();
        _mockProvider = Substitute.For<ILLMProvider>();

        _mockProvider.IsEnabled.Returns(true);
        _mockDictionaryService.BuildContextAsync(Arg.Any<CancellationToken>()).Returns(string.Empty);
        _mockDictionaryService.ApplyReplacementsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(x => Task.FromResult((string)x[0]));
        _mockSettingsRepository.GetSetting(Arg.Any<string>()).Returns(Task.FromResult<SettingsModel?>(null));

        _service = new TranscriptionEnhancerService(
            _mockProviderFactory,
            _mockDictionaryService,
            _mockSettingsRepository,
            _mockLlmRepository,
            _mockLogger);
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_WhenProviderNotFound_ReturnsRawText()
    {
        var rawText = "um so the the meeting was uh good";
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(Task.FromResult<ILLMProvider?>(null));

        var result = await _service.EnhanceTranscriptionAsync(rawText, "en");

        result.ShouldBe(rawText);
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_WhenProviderDisabled_ReturnsRawText()
    {
        var rawText = "um so the the meeting was uh good";
        _mockProvider.IsEnabled.Returns(false);
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);

        var result = await _service.EnhanceTranscriptionAsync(rawText, "en");

        result.ShouldBe(rawText);
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_WhenExceptionOccurs_ReturnsRawText()
    {
        var rawText = "um so the the meeting was uh good";
        _mockProviderFactory
            .GetProviderForModelAsync(Arg.Any<string>())
            .Returns<ILLMProvider?>(_ => throw new InvalidOperationException("Network error"));

        var result = await _service.EnhanceTranscriptionAsync(rawText, "en");

        result.ShouldBe(rawText);
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_WhenProviderReturnsFailure_ReturnsRawText()
    {
        var rawText = "um so the the meeting was uh good";
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>()).Returns(new LLMProviderResponse
        {
            Success = false,
            Content = string.Empty,
            Error = "Provider error"
        });

        var result = await _service.EnhanceTranscriptionAsync(rawText, "en");

        result.ShouldBe(rawText);
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_WhenProviderReturnsEmptyContent_ReturnsRawText()
    {
        var rawText = "um so the the meeting was uh good";
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>()).Returns(new LLMProviderResponse
        {
            Success = true,
            Content = "   "
        });

        var result = await _service.EnhanceTranscriptionAsync(rawText, "en");

        result.ShouldBe(rawText);
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_WhenSuccessful_ReturnsEnhancedText()
    {
        var rawText = "um so the meeting was uh good";
        var enhancedText = "The meeting was good.";
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>()).Returns(new LLMProviderResponse
        {
            Success = true,
            Content = enhancedText
        });

        var result = await _service.EnhanceTranscriptionAsync(rawText, "en");

        result.ShouldBe(enhancedText);
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
            .ProcessAsync(Arg.Do<LLMProviderRequest>(r => capturedRequest = r), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = "The Klacks system is ready." });

        await _service.EnhanceTranscriptionAsync(rawText, "en");

        capturedRequest.ShouldNotBeNull();
        capturedRequest!.SystemPrompt.ShouldContain(dictionaryContext);
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_UsesModelIdFromSettings()
    {
        var rawText = "um some longer raw text";
        var customModelId = "claude-haiku-4-5";
        var setting = new SettingsModel { Type = SettingsConstants.ASSISTANT_TRANSCRIPTION_MODEL, Value = customModelId };

        _mockSettingsRepository.GetSetting(SettingsConstants.ASSISTANT_TRANSCRIPTION_MODEL)
            .Returns(Task.FromResult<SettingsModel?>(setting));
        _mockProviderFactory.GetProviderForModelAsync(customModelId).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>()).Returns(new LLMProviderResponse
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
        var rawText = "um some longer raw text";
        var setting = new SettingsModel { Type = SettingsConstants.ASSISTANT_TRANSCRIPTION_MODEL, Value = string.Empty };

        _mockSettingsRepository.GetSetting(SettingsConstants.ASSISTANT_TRANSCRIPTION_MODEL)
            .Returns(Task.FromResult<SettingsModel?>(setting));
        _mockProviderFactory.GetProviderForModelAsync(TranscriptionConstants.DefaultModelId).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>()).Returns(new LLMProviderResponse
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
        var rawText = "um some longer raw text";
        _mockSettingsRepository.GetSetting(Arg.Any<string>()).Returns(Task.FromResult<SettingsModel?>(null));
        _mockProviderFactory.GetProviderForModelAsync(TranscriptionConstants.DefaultModelId).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>()).Returns(new LLMProviderResponse
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
            .ProcessAsync(Arg.Do<LLMProviderRequest>(r => capturedRequest = r), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = "Das Meeting war gut." });

        await _service.EnhanceTranscriptionAsync(rawText, "de");

        capturedRequest.ShouldNotBeNull();
        capturedRequest!.Message.ShouldBe(rawText);
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_TrimsEnhancedText()
    {
        var rawText = "um some longer raw text";
        var paddedText = "  Enhanced text.  ";
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>()).Returns(new LLMProviderResponse
        {
            Success = true,
            Content = paddedText
        });

        var result = await _service.EnhanceTranscriptionAsync(rawText, "en");

        result.ShouldBe(paddedText.Trim());
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_ShouldUseOverrideModelId_WhenProvided()
    {
        var rawText = "um some longer raw text";
        var overrideModelId = "override-model";
        _mockProviderFactory.GetProviderForModelAsync(overrideModelId).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>()).Returns(new LLMProviderResponse
        {
            Success = true,
            Content = "Enhanced"
        });

        var result = await _service.EnhanceTranscriptionAsync(rawText, "de", overrideModelId);

        result.ShouldBe("Enhanced");
        await _mockProviderFactory.Received(1).GetProviderForModelAsync(overrideModelId);
        await _mockSettingsRepository.DidNotReceive().GetSetting(SettingsConstants.ASSISTANT_TRANSCRIPTION_MODEL);
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_ShortCleanText_SkipsLlmCall()
    {
        var rawText = "ja genau";
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);

        var result = await _service.EnhanceTranscriptionAsync(rawText, "de");

        result.ShouldBe(rawText);
        await _mockProvider.DidNotReceive().ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_ShortTextWithFiller_CallsLlm()
    {
        var rawText = "ähm ja";
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = "Ja." });

        var result = await _service.EnhanceTranscriptionAsync(rawText, "de");

        result.ShouldBe("Ja.");
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_ShortTextWithRepeatedWord_CallsLlm()
    {
        var rawText = "zeige zeige kunden";
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = "Zeige Kunden." });

        var result = await _service.EnhanceTranscriptionAsync(rawText, "de");

        result.ShouldBe("Zeige Kunden.");
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_WhenOutputImplausiblyLong_ReturnsRawText()
    {
        var rawText = "um hello there friend";
        var runaway = new string('x', 500);
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = runaway });

        var result = await _service.EnhanceTranscriptionAsync(rawText, "en");

        result.ShouldBe(rawText);
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_DisablesThinkingBudget()
    {
        var rawText = "um so the the meeting was uh good";
        LLMProviderRequest? capturedRequest = null;
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);
        _mockProvider
            .ProcessAsync(Arg.Do<LLMProviderRequest>(r => capturedRequest = r), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = "The meeting was good." });

        await _service.EnhanceTranscriptionAsync(rawText, "en");

        capturedRequest.ShouldNotBeNull();
        capturedRequest!.ThinkingBudgetTokens.ShouldBe(0);
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_JapaneseLocale_UsesJapaneseExamplesInPrompt()
    {
        var rawText = "えーと月曜日に何人の従業員が働いていますか";
        LLMProviderRequest? capturedRequest = null;
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);
        _mockProvider
            .ProcessAsync(Arg.Do<LLMProviderRequest>(r => capturedRequest = r), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = "月曜日に何人の従業員が働いていますか？" });

        await _service.EnhanceTranscriptionAsync(rawText, "ja");

        capturedRequest.ShouldNotBeNull();
        capturedRequest!.SystemPrompt.ShouldContain("田中太郎");
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_RegionalLocale_UsesBaseLanguageExamples()
    {
        var rawText = "ähm also das meeting war halt gut";
        LLMProviderRequest? capturedRequest = null;
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);
        _mockProvider
            .ProcessAsync(Arg.Do<LLMProviderRequest>(r => capturedRequest = r), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = "Das Meeting war gut." });

        await _service.EnhanceTranscriptionAsync(rawText, "de-CH");

        capturedRequest.ShouldNotBeNull();
        capturedRequest!.SystemPrompt.ShouldContain("Hans Müller");
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_UnknownLocale_FallsBackToEnglishExamples()
    {
        var rawText = "um so the the meeting was uh good";
        LLMProviderRequest? capturedRequest = null;
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);
        _mockProvider
            .ProcessAsync(Arg.Do<LLMProviderRequest>(r => capturedRequest = r), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = "The meeting was good." });

        await _service.EnhanceTranscriptionAsync(rawText, "xx");

        capturedRequest.ShouldNotBeNull();
        capturedRequest!.SystemPrompt.ShouldContain("shift plan for next week");
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_LongCjkTextWithoutSpaces_CallsLlm()
    {
        var rawText = "新しい従業員を作成してください名前は田中太郎です";
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = "新しい従業員を作成して、名前は田中太郎です。" });

        var result = await _service.EnhanceTranscriptionAsync(rawText, "ja");

        result.ShouldBe("新しい従業員を作成して、名前は田中太郎です。");
        await _mockProvider.Received(1).ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_ShortCjkConfirmation_SkipsLlmCall()
    {
        var rawText = "はい";
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);

        var result = await _service.EnhanceTranscriptionAsync(rawText, "ja");

        result.ShouldBe(rawText);
        await _mockProvider.DidNotReceive().ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_WhenProviderTimesOut_ReturnsRawText()
    {
        var rawText = "um so the the meeting was uh good";
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<LLMProviderResponse>>(_ => throw new TaskCanceledException("Timed out"));

        var result = await _service.EnhanceTranscriptionAsync(rawText, "en");

        result.ShouldBe(rawText);
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_WhenCallerCancels_Throws()
    {
        var rawText = "um some longer raw text";
        using var callerCts = new CancellationTokenSource();
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<LLMProviderResponse>>(_ =>
            {
                callerCts.Cancel();
                throw new OperationCanceledException(callerCts.Token);
            });

        await Should.ThrowAsync<OperationCanceledException>(
            () => _service.EnhanceTranscriptionAsync(rawText, "en", null, callerCts.Token));
    }

    [TestCase("custom:2f5f3c9a-9d1a-4b8e-8f0e-1c2d3e4f5a6b")]
    [TestCase("groq-whisper")]
    [TestCase("deepgram")]
    [TestCase("assemblyai")]
    public async Task EnhanceTranscriptionAsync_WhenSttEngineIsServerSide_SkipsLlmCallAndReturnsPreprocessed(string engine)
    {
        var rawText = "um so the the meeting was uh good";
        var setting = new SettingsModel { Type = SettingsConstants.ASSISTANT_STT_ENGINE, Value = engine };
        _mockSettingsRepository.GetSetting(SettingsConstants.ASSISTANT_STT_ENGINE)
            .Returns(Task.FromResult<SettingsModel?>(setting));
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);

        var result = await _service.EnhanceTranscriptionAsync(rawText, "en");

        result.ShouldBe(rawText);
        await _mockProviderFactory.DidNotReceive().GetProviderForModelAsync(Arg.Any<string>());
        await _mockProvider.DidNotReceive().ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_WhenSttEngineIsBrowser_CallsLlm()
    {
        var rawText = "um so the the meeting was uh good";
        var setting = new SettingsModel { Type = SettingsConstants.ASSISTANT_STT_ENGINE, Value = SttConstants.Browser };
        _mockSettingsRepository.GetSetting(SettingsConstants.ASSISTANT_STT_ENGINE)
            .Returns(Task.FromResult<SettingsModel?>(setting));
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = "The meeting was good." });

        var result = await _service.EnhanceTranscriptionAsync(rawText, "en");

        result.ShouldBe("The meeting was good.");
        await _mockProvider.Received(1).ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_WhenSttEngineSettingIsEmpty_CallsLlm()
    {
        var rawText = "um so the the meeting was uh good";
        var setting = new SettingsModel { Type = SettingsConstants.ASSISTANT_STT_ENGINE, Value = string.Empty };
        _mockSettingsRepository.GetSetting(SettingsConstants.ASSISTANT_STT_ENGINE)
            .Returns(Task.FromResult<SettingsModel?>(setting));
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = "The meeting was good." });

        var result = await _service.EnhanceTranscriptionAsync(rawText, "en");

        result.ShouldBe("The meeting was good.");
        await _mockProvider.Received(1).ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_ShortNumericText_CallsLlm()
    {
        var rawText = "9, 9, 9";
        var setting = new SettingsModel { Type = SettingsConstants.ASSISTANT_STT_ENGINE, Value = SttConstants.Browser };
        _mockSettingsRepository.GetSetting(SettingsConstants.ASSISTANT_STT_ENGINE)
            .Returns(Task.FromResult<SettingsModel?>(setting));
        _mockProviderFactory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_mockProvider);
        _mockProvider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = "Nein, nein, nein." });

        var result = await _service.EnhanceTranscriptionAsync(rawText, "de");

        result.ShouldBe("Nein, nein, nein.");
        await _mockProvider.Received(1).ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnhanceTranscriptionAsync_SendsApiModelIdToProvider_NotInternalModelId()
    {
        var rawText = "um some longer raw text";
        var internalModelId = "gemini-3-1-flash-lite";
        var apiModelId = "gemini-3.1-flash-lite";
        var setting = new SettingsModel { Type = SettingsConstants.ASSISTANT_TRANSCRIPTION_MODEL, Value = internalModelId };
        LLMProviderRequest? capturedRequest = null;

        _mockSettingsRepository.GetSetting(SettingsConstants.ASSISTANT_TRANSCRIPTION_MODEL)
            .Returns(Task.FromResult<SettingsModel?>(setting));
        _mockProviderFactory.GetProviderForModelAsync(internalModelId).Returns(_mockProvider);
        _mockLlmRepository.GetModelByIdAsync(internalModelId)
            .Returns(Task.FromResult<LLMModel?>(new LLMModel { ModelId = internalModelId, ApiModelId = apiModelId }));
        _mockProvider
            .ProcessAsync(Arg.Do<LLMProviderRequest>(r => capturedRequest = r), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = "Some text." });

        await _service.EnhanceTranscriptionAsync(rawText, "en");

        capturedRequest.ShouldNotBeNull();
        capturedRequest!.ModelId.ShouldBe(apiModelId);
    }
}
