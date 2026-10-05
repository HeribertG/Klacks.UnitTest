// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Skills;

/// <summary>
/// The warn/block reaction to work on a statutory holiday is a setting like every other protection: the update
/// skill writes it under COMPLIANCE_ENFORCEMENT_HOLIDAY_WORK, normalised to the spelling the enforcement resolver
/// reads, and refuses anything that is neither warn nor block instead of storing a value the resolver ignores.
/// </summary>
[TestFixture]
public class UpdateComplianceEnforcementSettingsSkillTests
{
    private Dictionary<string, string> _store = null!;
    private UpdateComplianceEnforcementSettingsSkill _sut = null!;

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

        _sut = new UpdateComplianceEnforcementSettingsSkill(settingsRepository, Substitute.For<IUnitOfWork>(), encryption);
    }

    [TestCase("Block", ComplianceEnforcementModeValues.Block)]
    [TestCase(" warn ", ComplianceEnforcementModeValues.Warn)]
    public async Task HolidayWorkMode_IsStoredNormalized(string input, string expected)
    {
        var result = await _sut.ExecuteAsync(Context(), new Dictionary<string, object> { ["holidayWorkMode"] = input });

        result.Success.ShouldBeTrue(result.Message);
        _store[SettingKeys.ComplianceEnforcementHolidayWork].ShouldBe(expected);
    }

    [Test]
    public async Task HolidayWorkMode_UnknownValue_IsRefusedWithoutWriting()
    {
        var result = await _sut.ExecuteAsync(Context(), new Dictionary<string, object>
        {
            ["holidayWorkMode"] = "forbid",
            ["maxDailyHours"] = 10m,
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("holidayWorkMode");
        _store.ShouldBeEmpty();
    }

    [TestCase("defaultMode", SettingKeys.ComplianceEnforcementDefaultMode)]
    [TestCase("periodCapMode", SettingKeys.ComplianceEnforcementPeriodCap)]
    [TestCase("rollingAverageMode", SettingKeys.ComplianceEnforcementRollingAverage)]
    [TestCase("restDayRotationMode", SettingKeys.ComplianceEnforcementRestDayRotation)]
    [TestCase("counterRuleMode", SettingKeys.ComplianceEnforcementCounterRule)]
    [TestCase("compensatoryRestMode", SettingKeys.ComplianceEnforcementCompensatoryRest)]
    [TestCase("restrictedTimeWindowMode", SettingKeys.ComplianceEnforcementRestrictedTimeWindow)]
    public async Task EveryMode_IsStoredNormalized(string parameter, string key)
    {
        var result = await _sut.ExecuteAsync(Context(), new Dictionary<string, object> { [parameter] = " BLOCK " });

        result.Success.ShouldBeTrue(result.Message);
        _store[key].ShouldBe(ComplianceEnforcementModeValues.Block);
    }

    [TestCase("defaultMode")]
    [TestCase("periodCapMode")]
    [TestCase("restrictedTimeWindowMode")]
    public async Task EveryMode_UnknownValue_IsRefusedWithoutWriting(string parameter)
    {
        var result = await _sut.ExecuteAsync(Context(), new Dictionary<string, object>
        {
            [parameter] = "strict",
            ["maxDailyHours"] = 10m,
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain(parameter);
        _store.ShouldBeEmpty();
    }

    [TestCase("warn", ComplianceEnforcementModeValues.Warn)]
    [TestCase("Block", ComplianceEnforcementModeValues.Block)]
    [TestCase("forbid", null)]
    public void Normalize_AcceptsOnlyWarnAndBlock(string input, string? expected)
    {
        ComplianceEnforcementModeValues.Normalize(input).ShouldBe(expected);
    }

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Permissions.CanEditSettings },
    };
}
