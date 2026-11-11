// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using SettingKeys = Klacks.Api.Application.Constants.Settings;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Skills;

/// <summary>
/// The stage-3 settings skill only sets the deterministic local search; the language-model mode is retired, so a
/// request for it, for a model, or without a mode is refused without writing anything.
/// </summary>
[TestFixture]
public class UpdateWizardSettingsSkillTests
{
    private Dictionary<string, string> _store = null!;
    private UpdateWizardSettingsSkill _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new Dictionary<string, string>(StringComparer.Ordinal);
        var settingsRepository = Substitute.For<ISettingsRepository>();
        settingsRepository.GetSetting(Arg.Any<string>())
            .Returns(call => Task.FromResult(_store.TryGetValue(call.Arg<string>(), out var value)
                ? new SettingsEntity { Type = call.Arg<string>(), Value = value }
                : null));
        settingsRepository.AddSetting(Arg.Any<SettingsEntity>())
            .Returns(call =>
            {
                var setting = call.Arg<SettingsEntity>();
                _store[setting.Type] = setting.Value;
                return Task.FromResult(setting);
            });
        settingsRepository.PutSetting(Arg.Any<SettingsEntity>())
            .Returns(call =>
            {
                var setting = call.Arg<SettingsEntity>();
                _store[setting.Type] = setting.Value;
                return Task.FromResult(setting);
            });

        var encryption = Substitute.For<ISettingsEncryptionService>();
        encryption.ProcessForReading(Arg.Any<string>(), Arg.Any<string>()).Returns(call => call.ArgAt<string>(1));
        encryption.ProcessForStorage(Arg.Any<string>(), Arg.Any<string>()).Returns(call => call.ArgAt<string>(1));

        _sut = new UpdateWizardSettingsSkill(settingsRepository, Substitute.For<IUnitOfWork>(), encryption);
    }

    [Test]
    public async Task Mode_Deterministic_IsStoredNormalized()
    {
        var result = await _sut.ExecuteAsync(Context(), new Dictionary<string, object> { ["mode"] = " Deterministic " });

        result.Success.ShouldBeTrue(result.Message);
        _store[SettingKeys.HOLISTIC_HARMONIZER_MODE].ShouldBe("deterministic");
    }

    [Test]
    public async Task Mode_Llm_IsRefusedWithoutWriting()
    {
        var result = await _sut.ExecuteAsync(Context(), new Dictionary<string, object> { ["mode"] = "llm" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("retired");
        _store.ShouldBeEmpty();
    }

    [Test]
    public async Task Model_IsRefusedWithoutWriting_EvenWithDeterministicMode()
    {
        var result = await _sut.ExecuteAsync(Context(), new Dictionary<string, object>
        {
            ["mode"] = "deterministic",
            ["llmModelId"] = "gemini-25-flash",
        });

        result.Success.ShouldBeFalse();
        _store.ShouldBeEmpty();
    }

    [Test]
    public async Task MissingMode_IsRefusedWithoutWriting()
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
