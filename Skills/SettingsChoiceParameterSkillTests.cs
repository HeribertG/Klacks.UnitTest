// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.UnitTest.TestHelpers;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Skills;

/// <summary>
/// The rate-mode, stacking and overtime-basis parameters of the surcharge and overtime update skills are
/// choices. The settings UI compares the stored text exactly and the readers fall back to Multiplier/Day for
/// anything unknown, so the skills must store the canonical lowercase spelling and refuse an unknown value
/// without writing anything — before, "Fixbetrag" was saved, reported as verified and then silently ignored.
/// The seed must offer the model exactly the values the skills accept.
/// </summary>
[TestFixture]
public class SettingsChoiceParameterSkillTests
{
    private static readonly string[] SkillSeedsPath =
        ["Klacks.Api", "Application", "Skills", "Definitions", "skill-seeds.json"];

    private Dictionary<string, string> _store = null!;
    private ISettingsRepository _settingsRepository = null!;
    private ISettingsEncryptionService _encryption = null!;

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
            .Returns(call => Store(call.Arg<SettingsEntity>()));
        _settingsRepository.PutSetting(Arg.Any<SettingsEntity>())
            .Returns(call => Store(call.Arg<SettingsEntity>()));

        _encryption = Substitute.For<ISettingsEncryptionService>();
        _encryption.ProcessForReading(Arg.Any<string>(), Arg.Any<string>()).Returns(call => call.ArgAt<string>(1));
        _encryption.ProcessForStorage(Arg.Any<string>(), Arg.Any<string>()).Returns(call => call.ArgAt<string>(1));
    }

    [TestCase("nightRateMode", SettingKeys.SurchargeNightRateMode, "FixedPerHour", CompanyRuleParameterValues.RateModeFixedPerHour)]
    [TestCase("we3RateMode", SettingKeys.SurchargeWE3RateMode, " fixedpershift ", CompanyRuleParameterValues.RateModeFixedPerShift)]
    [TestCase("stackingMode", SettingKeys.SurchargeStackingMode, "Additive", CompanyRuleParameterValues.StackingModeAdditive)]
    public async Task SurchargeChoice_IsStoredInCanonicalSpelling(string parameter, string key, string input, string expected)
    {
        var result = await SurchargeSkill().ExecuteAsync(Context(), new Dictionary<string, object> { [parameter] = input });

        result.Success.ShouldBeTrue(result.Message);
        _store[key].ShouldBe(expected);
    }

    [TestCase("nightRateMode", "Fixbetrag")]
    [TestCase("holidayRateMode", "fixed")]
    [TestCase("stackingMode", "sum")]
    public async Task SurchargeChoice_UnknownValue_IsRefusedWithoutWriting(string parameter, string input)
    {
        var result = await SurchargeSkill().ExecuteAsync(Context(), new Dictionary<string, object>
        {
            [parameter] = input,
            ["nightMinimumPerHour"] = 0.5m,
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain(parameter);
        result.Message.ShouldContain(input);
        _store.ShouldBeEmpty();
    }

    [TestCase("basis", SettingKeys.OvertimeBasis, "Week", CompanyRuleParameterValues.OvertimeBasisWeek)]
    [TestCase("rateMode", SettingKeys.OvertimeRateMode, "FIXEDPERHOUR", CompanyRuleParameterValues.RateModeFixedPerHour)]
    public async Task OvertimeChoice_IsStoredInCanonicalSpelling(string parameter, string key, string input, string expected)
    {
        var result = await OvertimeSkill().ExecuteAsync(Context(), new Dictionary<string, object> { [parameter] = input });

        result.Success.ShouldBeTrue(result.Message);
        _store[key].ShouldBe(expected);
    }

    [TestCase("rateMode", "fixedpershift")]
    [TestCase("basis", "month")]
    public async Task OvertimeChoice_UnknownValue_IsRefusedWithoutWriting(string parameter, string input)
    {
        var result = await OvertimeSkill().ExecuteAsync(Context(), new Dictionary<string, object>
        {
            [parameter] = input,
            ["tier1Rate"] = 1.25m,
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain(parameter);
        _store.ShouldBeEmpty();
    }

    [TestCase("update_surcharge_mode_settings", "nightRateMode")]
    [TestCase("update_surcharge_mode_settings", "holidayRateMode")]
    [TestCase("update_surcharge_mode_settings", "we1RateMode")]
    [TestCase("update_surcharge_mode_settings", "we2RateMode")]
    [TestCase("update_surcharge_mode_settings", "we3RateMode")]
    [TestCase("update_surcharge_mode_settings", "stackingMode")]
    [TestCase("update_overtime_settings", "basis")]
    [TestCase("update_overtime_settings", "rateMode")]
    public void Seed_OffersExactlyTheAcceptedValues(string skillName, string parameterName)
    {
        var expected = parameterName switch
        {
            "stackingMode" => CompanyRuleParameterValues.StackingModes,
            "basis" => CompanyRuleParameterValues.OvertimeBases,
            "rateMode" => CompanyRuleParameterValues.OvertimeRateModes,
            _ => CompanyRuleParameterValues.SurchargeRateModes,
        };

        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryRootLocator.RequireFile(SkillSeedsPath)));
        var parameter = document.RootElement.GetProperty("skills").EnumerateArray()
            .Where(skill => skill.GetProperty("name").GetString() == skillName)
            .SelectMany(skill => skill.GetProperty("parameters").EnumerateArray())
            .Single(p => p.GetProperty("name").GetString() == parameterName);

        parameter.GetProperty("type").GetString().ShouldBe("Enum");
        parameter.GetProperty("enumValues").EnumerateArray().Select(v => v.GetString()).ShouldBe(expected);
    }

    private UpdateSurchargeModeSettingsSkill SurchargeSkill() =>
        new(_settingsRepository, Substitute.For<IUnitOfWork>(), _encryption);

    private UpdateOvertimeSettingsSkill OvertimeSkill() =>
        new(_settingsRepository, Substitute.For<IUnitOfWork>(), _encryption);

    private Task<SettingsEntity> Store(SettingsEntity setting)
    {
        _store[setting.Type] = setting.Value;
        return Task.FromResult(setting);
    }

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Permissions.CanEditSettings },
    };
}
