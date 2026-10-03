// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using SettingKeys = Klacks.Api.Application.Constants.Settings;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Skills;

/// <summary>
/// The stage-3 settings skill switches between the deterministic local search and the LLM engine and sets the
/// model; at least one of both must be given, unknown modes and models are refused without writing anything.
/// </summary>
[TestFixture]
public class UpdateWizardSettingsSkillTests
{
    private const string KnownModel = "gemini-25-flash";

    private ISettingsRepository _settingsRepository = null!;
    private ILLMRepository _llmRepository = null!;
    private Dictionary<string, string> _store = null!;
    private UpdateWizardSettingsSkill _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new Dictionary<string, string>(StringComparer.Ordinal);
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _settingsRepository.GetSetting(Arg.Any<string>())
            .Returns(call => Task.FromResult(_store.TryGetValue(call.Arg<string>(), out var value)
                ? new SettingsEntity { Type = call.Arg<string>(), Value = value }
                : null));
        _settingsRepository.AddSetting(Arg.Any<SettingsEntity>())
            .Returns(call =>
            {
                var setting = call.Arg<SettingsEntity>();
                _store[setting.Type] = setting.Value;
                return Task.FromResult(setting);
            });
        _settingsRepository.PutSetting(Arg.Any<SettingsEntity>())
            .Returns(call =>
            {
                var setting = call.Arg<SettingsEntity>();
                _store[setting.Type] = setting.Value;
                return Task.FromResult(setting);
            });

        _llmRepository = Substitute.For<ILLMRepository>();
        _llmRepository.GetModelsAsync(Arg.Any<bool>())
            .Returns(new List<LLMModel> { new() { ModelId = KnownModel } });

        var encryption = Substitute.For<ISettingsEncryptionService>();
        encryption.ProcessForReading(Arg.Any<string>(), Arg.Any<string>()).Returns(call => call.ArgAt<string>(1));
        encryption.ProcessForStorage(Arg.Any<string>(), Arg.Any<string>()).Returns(call => call.ArgAt<string>(1));

        _sut = new UpdateWizardSettingsSkill(_settingsRepository, Substitute.For<IUnitOfWork>(), _llmRepository, encryption);
    }

    [Test]
    public async Task Mode_Llm_IsStoredNormalized()
    {
        var result = await _sut.ExecuteAsync(Context(), new Dictionary<string, object> { ["mode"] = " LLM " });

        result.Success.ShouldBeTrue(result.Message);
        _store[SettingKeys.HOLISTIC_HARMONIZER_MODE].ShouldBe("llm");
        _store.ShouldNotContainKey(SettingKeys.HOLISTIC_HARMONIZER_LLM_MODEL);
    }

    [Test]
    public async Task ModeAndModel_AreBothStored()
    {
        var result = await _sut.ExecuteAsync(Context(), new Dictionary<string, object>
        {
            ["mode"] = "llm",
            ["llmModelId"] = KnownModel,
        });

        result.Success.ShouldBeTrue(result.Message);
        _store[SettingKeys.HOLISTIC_HARMONIZER_MODE].ShouldBe("llm");
        _store[SettingKeys.HOLISTIC_HARMONIZER_LLM_MODEL].ShouldBe(KnownModel);
    }

    [Test]
    public async Task UnknownMode_IsRefusedWithoutWriting()
    {
        var result = await _sut.ExecuteAsync(Context(), new Dictionary<string, object> { ["mode"] = "vision" });

        result.Success.ShouldBeFalse();
        _store.ShouldBeEmpty();
    }

    [Test]
    public async Task NeitherModeNorModel_IsRefused()
    {
        var result = await _sut.ExecuteAsync(Context(), new Dictionary<string, object>());

        result.Success.ShouldBeFalse();
        _store.ShouldBeEmpty();
    }

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "test",
        UserPermissions = [],
    };
}
