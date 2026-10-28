// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the "local" engine value of UpdateSpeechSettingsSkill: it is stored as the custom provider id of
/// the installed local speech recognition when that provider row is enabled (any casing), refused with the
/// install hint before anything is written otherwise, and leaves ordinary engine values untouched.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Skills;
using SettingKeys = Klacks.Api.Application.Constants.Settings;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Skills;

[TestFixture]
public class UpdateSpeechSettingsSkillTests
{
    private const string SttEngineParameter = "sttEngine";
    private const string OutputModeParameter = "outputMode";
    private const string InstallHint = "Install the local speech recognition first (install_whisper_plugin).";

    private ISettingsRepository _settingsRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ICustomSttProviderResolver _resolver = null!;
    private Dictionary<string, SettingsModel> _store = null!;
    private UpdateSpeechSettingsSkill _skill = null!;

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "admin",
        UserPermissions = new List<string>()
    };

    [SetUp]
    public void SetUp()
    {
        _store = new Dictionary<string, SettingsModel>();
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _settingsRepository.GetSetting(Arg.Any<string>())
            .Returns(call => _store.GetValueOrDefault(call.Arg<string>()));
        _settingsRepository.AddSetting(Arg.Any<SettingsModel>()).Returns(call =>
        {
            var added = call.Arg<SettingsModel>();
            _store[added.Type] = added;
            return added;
        });
        _settingsRepository.PutSetting(Arg.Any<SettingsModel>()).Returns(call => call.Arg<SettingsModel>());
        _unitOfWork = Substitute.For<IUnitOfWork>();
        var encryption = Substitute.For<ISettingsEncryptionService>();
        encryption.ProcessForStorage(Arg.Any<string>(), Arg.Any<string>()).Returns(call => call.ArgAt<string>(1));
        encryption.ProcessForReading(Arg.Any<string>(), Arg.Any<string>()).Returns(call => call.ArgAt<string>(1));
        _resolver = Substitute.For<ICustomSttProviderResolver>();
        _skill = new UpdateSpeechSettingsSkill(_settingsRepository, _unitOfWork, encryption, _resolver, PlaygroundOff.Mode);
    }

    private void InstallLocalProvider() =>
        _resolver.TryResolveAsync(WhisperPluginConstants.ProviderEngineValue, Arg.Any<CancellationToken>())
            .Returns(new CustomSttProvider { Id = WhisperPluginConstants.ProviderId, IsEnabled = true });

    [TestCase("local")]
    [TestCase("LOCAL")]
    [TestCase(" Local ")]
    public async Task ExecuteAsync_LocalEngineWithInstalledProvider_StoresTheCustomProviderId(string value)
    {
        InstallLocalProvider();

        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object> { [SttEngineParameter] = value });

        result.Success.ShouldBeTrue();
        _store[SettingKeys.ASSISTANT_STT_ENGINE].Value.ShouldBe($"custom:{WhisperPluginConstants.ProviderId}");
    }

    [Test]
    public async Task ExecuteAsync_LocalEngineWithoutInstalledProvider_ReturnsInstallHintAndWritesNothing()
    {
        _resolver.TryResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((CustomSttProvider?)null);

        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object>
        {
            [SttEngineParameter] = WhisperPluginConstants.LocalEngineValue,
            [OutputModeParameter] = "spoken",
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(InstallHint);
        _store.ShouldBeEmpty();
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task ExecuteAsync_OtherEngineValue_IsStoredAsGivenWithoutLookingUpTheLocalProvider()
    {
        var result = await _skill.ExecuteAsync(
            Context(), new Dictionary<string, object> { [SttEngineParameter] = SttProviderConstants.Deepgram });

        result.Success.ShouldBeTrue();
        _store[SettingKeys.ASSISTANT_STT_ENGINE].Value.ShouldBe(SttProviderConstants.Deepgram);
        await _resolver.DidNotReceiveWithAnyArgs().TryResolveAsync(default!, default);
    }
}
