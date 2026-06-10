// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant;
using ApiSettings = Klacks.Api.Application.Constants.Settings;
using SettingRow = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class TtsApiKeyResolverTests
{
    private ISettingsRepository _settingsRepository = null!;
    private ISettingsEncryptionService _encryptionService = null!;
    private ILLMRepository _llmRepository = null!;
    private TtsApiKeyResolver _sut = null!;

    [SetUp]
    public void Setup()
    {
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _encryptionService = Substitute.For<ISettingsEncryptionService>();
        _llmRepository = Substitute.For<ILLMRepository>();
        _sut = new TtsApiKeyResolver(_settingsRepository, _encryptionService, _llmRepository);
    }

    [Test]
    public async Task DedicatedSetting_IsDecryptedAndReturned()
    {
        _settingsRepository.GetSetting(ApiSettings.ASSISTANT_TTS_API_KEY_ELEVENLABS)
            .Returns(new SettingRow { Type = ApiSettings.ASSISTANT_TTS_API_KEY_ELEVENLABS, Value = "ENC:abc" });
        _encryptionService.Decrypt("ENC:abc").Returns("plain-key");

        var key = await _sut.ResolveAsync(TtsProviderConstants.ElevenLabs);

        Assert.That(key, Is.EqualTo("plain-key"));
        await _llmRepository.DidNotReceive().GetProviderByIdAsync(Arg.Any<string>());
    }

    [Test]
    public async Task MissingDedicatedSetting_FallsBackToLlmProviderRow()
    {
        _settingsRepository.GetSetting(ApiSettings.ASSISTANT_TTS_API_KEY_GOOGLE)
            .Returns((SettingRow?)null);
        _llmRepository.GetProviderByIdAsync(TtsProviderConstants.Google)
            .Returns(new LLMProvider { ProviderId = TtsProviderConstants.Google, ApiKey = "legacy-key" });

        var key = await _sut.ResolveAsync(TtsProviderConstants.Google);

        Assert.That(key, Is.EqualTo("legacy-key"));
    }

    [Test]
    public async Task NoKeyAnywhere_ReturnsNull()
    {
        _settingsRepository.GetSetting(Arg.Any<string>()).Returns((SettingRow?)null);
        _llmRepository.GetProviderByIdAsync(Arg.Any<string>()).Returns((LLMProvider?)null);

        var key = await _sut.ResolveAsync(TtsProviderConstants.OpenAi);

        Assert.That(key, Is.Null);
    }

    [Test]
    public async Task UnknownProvider_UsesOnlyLlmProviderFallback()
    {
        _llmRepository.GetProviderByIdAsync("edge")
            .Returns(new LLMProvider { ProviderId = "edge", ApiKey = "edge-key" });

        var key = await _sut.ResolveAsync("edge");

        Assert.That(key, Is.EqualTo("edge-key"));
        await _settingsRepository.DidNotReceive().GetSetting(Arg.Any<string>());
    }
}
