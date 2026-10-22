// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the engine report of GetSpeechSettingsSkill: the stored custom id of the installed local speech
/// recognition is reported as "local" with the installed engine alias, other engines and a disabled or missing
/// local provider are reported as stored.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Skills;
using SettingKeys = Klacks.Api.Application.Constants.Settings;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Application.Skills;

[TestFixture]
public class GetSpeechSettingsSkillTests
{
    private ISettingsRepository _settingsRepository = null!;
    private ICustomSttProviderResolver _resolver = null!;
    private GetSpeechSettingsSkill _skill = null!;

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
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _resolver = Substitute.For<ICustomSttProviderResolver>();
        _skill = new GetSpeechSettingsSkill(_settingsRepository, _resolver);
    }

    private void StoreEngine(string value) =>
        _settingsRepository.GetSetting(SettingKeys.ASSISTANT_STT_ENGINE)
            .Returns(new SettingsModel { Type = SettingKeys.ASSISTANT_STT_ENGINE, Value = value });

    private async Task<(string Engine, string LocalEngine)> ReadEngineAsync()
    {
        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object>());
        result.Success.ShouldBeTrue();
        var type = result.Data!.GetType();
        return ((string)type.GetProperty("SttEngine")!.GetValue(result.Data)!,
            (string)type.GetProperty("LocalEngine")!.GetValue(result.Data)!);
    }

    [Test]
    public async Task ExecuteAsync_InstalledLocalProvider_ReportsLocalWithTheEngineAlias()
    {
        StoreEngine(WhisperPluginConstants.ProviderEngineValue);
        _resolver.TryResolveAsync(WhisperPluginConstants.ProviderEngineValue, Arg.Any<CancellationToken>())
            .Returns(new CustomSttProvider
            {
                Id = WhisperPluginConstants.ProviderId,
                IsEnabled = true,
                LanguageModel = WhisperPluginConstants.ParakeetModelId,
            });

        var (engine, localEngine) = await ReadEngineAsync();

        engine.ShouldBe(WhisperPluginConstants.LocalEngineValue);
        localEngine.ShouldBe(WhisperPluginConstants.ModelAliasParakeet);
    }

    [Test]
    public async Task ExecuteAsync_LocalProviderNoLongerEnabled_ReportsTheStoredValue()
    {
        StoreEngine(WhisperPluginConstants.ProviderEngineValue);
        _resolver.TryResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((CustomSttProvider?)null);

        var (engine, localEngine) = await ReadEngineAsync();

        engine.ShouldBe(WhisperPluginConstants.ProviderEngineValue);
        localEngine.ShouldBe(string.Empty);
    }

    [Test]
    public async Task ExecuteAsync_OtherEngine_IsReportedAsStoredWithoutProviderLookup()
    {
        StoreEngine(SttProviderConstants.GroqWhisper);

        var (engine, localEngine) = await ReadEngineAsync();

        engine.ShouldBe(SttProviderConstants.GroqWhisper);
        localEngine.ShouldBe(string.Empty);
        await _resolver.DidNotReceiveWithAnyArgs().TryResolveAsync(default!, default);
    }

    [Test]
    public async Task ExecuteAsync_NoStoredEngine_ReportsEmpty()
    {
        var (engine, localEngine) = await ReadEngineAsync();

        engine.ShouldBe(string.Empty);
        localEngine.ShouldBe(string.Empty);
    }
}
