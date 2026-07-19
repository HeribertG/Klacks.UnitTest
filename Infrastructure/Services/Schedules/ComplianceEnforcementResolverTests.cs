// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ComplianceEnforcementResolver proving the admin-editable enforcement settings are
/// effective: a per-rule COMPLIANCE_ENFORCEMENT_* value wins over the default mode, the default mode
/// applies when the per-rule key is absent, Warn is the hard fallback when nothing is configured, an
/// unrecognized per-rule value (typo) falls back to the default mode instead of silently weakening
/// to Warn, every one of the eleven rule names reads its own setting key, and the supervisor-override
/// flag follows its setting with a default of true — also when its value is unparseable.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.Extensions.Logging;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class ComplianceEnforcementResolverTests
{
    private const string BlockValue = "block";
    private const string BlockValueUppercase = "BLOCK";
    private const string WarnValue = "warn";
    private const string UnrecognizedValue = "not-a-mode";
    private const string WhitespaceValue = "   ";
    private const string UnknownRuleName = "someUnknownRule";
    private const string TrueValue = "true";
    private const string TrueValueUppercase = "TRUE";
    private const string FalseValue = "false";
    private const string NonBooleanValue = "yes";

    private ISettingsReader _settingsReader = null!;
    private ComplianceEnforcementResolver _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _settingsReader = Substitute.For<ISettingsReader>();
        _settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsModel?)null);
        _sut = new ComplianceEnforcementResolver(_settingsReader, Substitute.For<ILogger<ComplianceEnforcementResolver>>());
    }

    private void StubSetting(string key, string value) =>
        _settingsReader.GetSetting(key).Returns(new SettingsModel { Id = Guid.NewGuid(), Type = key, Value = value });

    [Test]
    public async Task GetModeAsync_PerRuleBlock_ReturnsBlockWithoutConsultingDefault()
    {
        StubSetting(SettingKeys.ComplianceEnforcementMaxDailyHours, BlockValue);

        var mode = await _sut.GetModeAsync(ComplianceRuleNames.MaxDailyHours);

        mode.ShouldBe(RuleEnforcementMode.Block);
        await _settingsReader.DidNotReceive().GetSetting(SettingKeys.ComplianceEnforcementDefaultMode);
    }

    [Test]
    public async Task GetModeAsync_PerRuleBlockUppercase_ReturnsBlock()
    {
        StubSetting(SettingKeys.ComplianceEnforcementMaxDailyHours, BlockValueUppercase);

        var mode = await _sut.GetModeAsync(ComplianceRuleNames.MaxDailyHours);

        mode.ShouldBe(RuleEnforcementMode.Block);
    }

    [Test]
    public async Task GetModeAsync_PerRuleAbsentDefaultBlock_ReturnsBlock()
    {
        StubSetting(SettingKeys.ComplianceEnforcementDefaultMode, BlockValue);

        var mode = await _sut.GetModeAsync(ComplianceRuleNames.MinRestHours);

        mode.ShouldBe(RuleEnforcementMode.Block);
    }

    [Test]
    public async Task GetModeAsync_NothingConfigured_ReturnsWarn()
    {
        var mode = await _sut.GetModeAsync(ComplianceRuleNames.PeriodCap);

        mode.ShouldBe(RuleEnforcementMode.Warn);
    }

    [Test]
    public async Task GetModeAsync_UnrecognizedPerRuleValue_FallsBackToDefaultMode()
    {
        StubSetting(SettingKeys.ComplianceEnforcementMaxWeeklyHours, UnrecognizedValue);
        StubSetting(SettingKeys.ComplianceEnforcementDefaultMode, BlockValue);

        var mode = await _sut.GetModeAsync(ComplianceRuleNames.MaxWeeklyHours);

        mode.ShouldBe(RuleEnforcementMode.Block);
        await _settingsReader.Received(1).GetSetting(SettingKeys.ComplianceEnforcementDefaultMode);
    }

    [Test]
    public async Task GetModeAsync_UnrecognizedPerRuleValueWithoutDefault_ReturnsWarn()
    {
        StubSetting(SettingKeys.ComplianceEnforcementMaxWeeklyHours, UnrecognizedValue);

        var mode = await _sut.GetModeAsync(ComplianceRuleNames.MaxWeeklyHours);

        mode.ShouldBe(RuleEnforcementMode.Warn);
    }

    [Test]
    public async Task GetModeAsync_WhitespacePerRuleValue_FallsThroughToDefaultMode()
    {
        StubSetting(SettingKeys.ComplianceEnforcementMaxWeeklyHours, WhitespaceValue);
        StubSetting(SettingKeys.ComplianceEnforcementDefaultMode, BlockValue);

        var mode = await _sut.GetModeAsync(ComplianceRuleNames.MaxWeeklyHours);

        mode.ShouldBe(RuleEnforcementMode.Block);
    }

    [Test]
    public async Task GetModeAsync_UnrecognizedDefaultModeValue_ReturnsWarn()
    {
        StubSetting(SettingKeys.ComplianceEnforcementDefaultMode, UnrecognizedValue);

        var mode = await _sut.GetModeAsync(ComplianceRuleNames.MaxConsecutiveDays);

        mode.ShouldBe(RuleEnforcementMode.Warn);
    }

    [Test]
    public async Task GetModeAsync_UnknownRuleName_UsesDefaultMode()
    {
        StubSetting(SettingKeys.ComplianceEnforcementDefaultMode, BlockValue);

        var mode = await _sut.GetModeAsync(UnknownRuleName);

        mode.ShouldBe(RuleEnforcementMode.Block);
    }

    [Test]
    public async Task GetModeAsync_SameRuleDifferentSettingValue_YieldsDifferentMode()
    {
        StubSetting(SettingKeys.ComplianceEnforcementCounterRule, WarnValue);
        var modeA = await _sut.GetModeAsync(ComplianceRuleNames.CounterRule);

        StubSetting(SettingKeys.ComplianceEnforcementCounterRule, BlockValue);
        var modeB = await _sut.GetModeAsync(ComplianceRuleNames.CounterRule);

        modeA.ShouldBe(RuleEnforcementMode.Warn);
        modeB.ShouldBe(RuleEnforcementMode.Block);
    }

    [TestCase(ComplianceRuleNames.MaxDailyHours, SettingKeys.ComplianceEnforcementMaxDailyHours)]
    [TestCase(ComplianceRuleNames.MaxWeeklyHours, SettingKeys.ComplianceEnforcementMaxWeeklyHours)]
    [TestCase(ComplianceRuleNames.MinRestHours, SettingKeys.ComplianceEnforcementMinRestHours)]
    [TestCase(ComplianceRuleNames.MinRestDays, SettingKeys.ComplianceEnforcementMinRestDays)]
    [TestCase(ComplianceRuleNames.MaxConsecutiveDays, SettingKeys.ComplianceEnforcementMaxConsecutiveDays)]
    [TestCase(ComplianceRuleNames.PeriodCap, SettingKeys.ComplianceEnforcementPeriodCap)]
    [TestCase(ComplianceRuleNames.RollingAverage, SettingKeys.ComplianceEnforcementRollingAverage)]
    [TestCase(ComplianceRuleNames.RestDayRotation, SettingKeys.ComplianceEnforcementRestDayRotation)]
    [TestCase(ComplianceRuleNames.CounterRule, SettingKeys.ComplianceEnforcementCounterRule)]
    [TestCase(ComplianceRuleNames.CompensatoryRest, SettingKeys.ComplianceEnforcementCompensatoryRest)]
    [TestCase(ComplianceRuleNames.RestrictedTimeWindow, SettingKeys.ComplianceEnforcementRestrictedTimeWindow)]
    public async Task GetModeAsync_EveryRuleName_ReadsItsOwnPerRuleKey(string ruleName, string expectedKey)
    {
        StubSetting(expectedKey, BlockValue);

        var mode = await _sut.GetModeAsync(ruleName);

        mode.ShouldBe(RuleEnforcementMode.Block);
        await _settingsReader.Received(1).GetSetting(expectedKey);
        await _settingsReader.DidNotReceive().GetSetting(SettingKeys.ComplianceEnforcementDefaultMode);
    }

    [Test]
    public async Task IsSupervisorOverrideAllowedAsync_SettingTrue_ReturnsTrue()
    {
        StubSetting(SettingKeys.ComplianceEnforcementAllowSupervisorOverride, TrueValue);

        (await _sut.IsSupervisorOverrideAllowedAsync()).ShouldBeTrue();
    }

    [Test]
    public async Task IsSupervisorOverrideAllowedAsync_SettingTrueUppercase_ReturnsTrue()
    {
        StubSetting(SettingKeys.ComplianceEnforcementAllowSupervisorOverride, TrueValueUppercase);

        (await _sut.IsSupervisorOverrideAllowedAsync()).ShouldBeTrue();
    }

    [Test]
    public async Task IsSupervisorOverrideAllowedAsync_SettingFalse_ReturnsFalse()
    {
        StubSetting(SettingKeys.ComplianceEnforcementAllowSupervisorOverride, FalseValue);

        (await _sut.IsSupervisorOverrideAllowedAsync()).ShouldBeFalse();
    }

    [Test]
    public async Task IsSupervisorOverrideAllowedAsync_SettingAbsent_DefaultsToTrue()
    {
        (await _sut.IsSupervisorOverrideAllowedAsync()).ShouldBeTrue();
    }

    [Test]
    public async Task IsSupervisorOverrideAllowedAsync_NonBooleanValue_FallsBackToDefaultTrue()
    {
        StubSetting(SettingKeys.ComplianceEnforcementAllowSupervisorOverride, NonBooleanValue);

        (await _sut.IsSupervisorOverrideAllowedAsync()).ShouldBeTrue();
    }
}
