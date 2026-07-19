// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for OvertimeConfigResolver proving the admin-editable OVERTIME_* settings and the
/// SchedulingRule/revision ladder are effective, stage by stage: settings alone build the tier ladder,
/// editing a setting value changes the resolved tiers (A/B), a referenced rule with a complete tier 1
/// overrides the settings entirely, a dated revision replaces the rule's base ladder from its ValidFrom
/// onward, an applicable revision without an overtime block falls through to the settings (never the
/// base rule ladder), and without any tier settings the contract's OvertimeThreshold feeds tier 1.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class OvertimeConfigResolverTests
{
    private const string BasisWeekValue = "week";
    private const string BasisDayValue = "day";
    private const string RateModeFixedPerHourValue = "fixedperhour";
    private const string RateModeFixedPerShiftValue = "fixedpershift";

    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 7, 15);

    private DataBaseContext _context = null!;
    private IClientContractDataProvider _contractDataProvider = null!;
    private OvertimeConfigResolver _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _contractDataProvider = Substitute.For<IClientContractDataProvider>();
        _contractDataProvider
            .GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData());

        _sut = new OvertimeConfigResolver(_context, _contractDataProvider);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private void SeedSetting(string type, string value)
    {
        _context.Settings.Add(new SettingsModel { Id = Guid.NewGuid(), Type = type, Value = value });
        _context.SaveChanges();
    }

    private void UpdateSetting(string type, string value)
    {
        var row = _context.Settings.Single(s => s.Type == type);
        row.Value = value;
        _context.SaveChanges();
    }

    private SchedulingRule SeedRule(
        decimal? tier1AfterHours,
        decimal? tier1Rate,
        OvertimeBasis? basis = null,
        SurchargeRateMode? rateMode = null)
    {
        var rule = new SchedulingRule
        {
            Id = Guid.NewGuid(),
            Name = "Industry preset",
            OvertimeTier1AfterHours = tier1AfterHours,
            OvertimeTier1Rate = tier1Rate,
            OvertimeBasis = basis,
            OvertimeRateMode = rateMode,
        };
        _context.SchedulingRules.Add(rule);
        _context.SaveChanges();
        return rule;
    }

    private void SeedRevision(
        Guid ruleId,
        DateOnly validFrom,
        decimal? tier1AfterHours,
        decimal? tier1Rate,
        OvertimeBasis? basis = null,
        decimal? nightRate = null)
    {
        _context.SchedulingRuleRateRevisions.Add(new SchedulingRuleRateRevision
        {
            Id = Guid.NewGuid(),
            SchedulingRuleId = ruleId,
            ValidFrom = validFrom,
            OvertimeTier1AfterHours = tier1AfterHours,
            OvertimeTier1Rate = tier1Rate,
            OvertimeBasis = basis,
            NightRate = nightRate,
        });
        _context.SaveChanges();
    }

    private void StubContract(EffectiveContractData data) =>
        _contractDataProvider
            .GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(data);

    [Test]
    public async Task ResolveAsync_SettingsOnly_BuildsLadderFromSettings()
    {
        SeedSetting(SettingKeys.OvertimeBasis, BasisWeekValue);
        SeedSetting(SettingKeys.OvertimeRateMode, RateModeFixedPerHourValue);
        SeedSetting(SettingKeys.OvertimeTier1AfterHours, "42");
        SeedSetting(SettingKeys.OvertimeTier1Rate, "0.25");
        SeedSetting(SettingKeys.OvertimeTier2AfterHours, "50");
        SeedSetting(SettingKeys.OvertimeTier2Rate, "0.5");

        var config = await _sut.ResolveAsync(ClientId, Day);

        config.Basis.ShouldBe(OvertimeBasis.Week);
        config.RateMode.ShouldBe(SurchargeRateMode.FixedPerHour);
        config.Tiers.Count.ShouldBe(2);
        config.Tiers[0].ShouldBe(new OvertimeTierConfig(42m, 0.25m, SurchargeType.Overtime1));
        config.Tiers[1].ShouldBe(new OvertimeTierConfig(50m, 0.5m, SurchargeType.Overtime2));
        await _contractDataProvider.DidNotReceiveWithAnyArgs()
            .GetEffectiveContractDataAsync(default, default, default);
    }

    [Test]
    public async Task ResolveAsync_TierSettingEdited_YieldsDifferentTiers()
    {
        SeedSetting(SettingKeys.OvertimeTier1AfterHours, "40");
        SeedSetting(SettingKeys.OvertimeTier1Rate, "0.25");

        var configA = await _sut.ResolveAsync(ClientId, Day);

        UpdateSetting(SettingKeys.OvertimeTier1AfterHours, "45");
        UpdateSetting(SettingKeys.OvertimeTier1Rate, "0.5");
        var configB = await _sut.ResolveAsync(ClientId, Day);

        configA.Tiers.ShouldHaveSingleItem().ShouldBe(new OvertimeTierConfig(40m, 0.25m, SurchargeType.Overtime1));
        configB.Tiers.ShouldHaveSingleItem().ShouldBe(new OvertimeTierConfig(45m, 0.5m, SurchargeType.Overtime1));
    }

    [Test]
    public async Task ResolveAsync_BasisSettingEdited_SwitchesWeekToDay()
    {
        SeedSetting(SettingKeys.OvertimeBasis, BasisWeekValue);
        SeedSetting(SettingKeys.OvertimeTier1AfterHours, "42");
        SeedSetting(SettingKeys.OvertimeTier1Rate, "0.25");

        var configA = await _sut.ResolveAsync(ClientId, Day);

        UpdateSetting(SettingKeys.OvertimeBasis, BasisDayValue);
        var configB = await _sut.ResolveAsync(ClientId, Day);

        configA.Basis.ShouldBe(OvertimeBasis.Week);
        configB.Basis.ShouldBe(OvertimeBasis.Day);
    }

    [Test]
    public async Task ResolveAsync_FixedPerShiftRateModeSetting_FallsBackToMultiplier()
    {
        SeedSetting(SettingKeys.OvertimeRateMode, RateModeFixedPerShiftValue);
        SeedSetting(SettingKeys.OvertimeTier1AfterHours, "42");
        SeedSetting(SettingKeys.OvertimeTier1Rate, "0.25");

        var config = await _sut.ResolveAsync(ClientId, Day);

        config.RateMode.ShouldBe(SurchargeRateMode.Multiplier);
    }

    [Test]
    public async Task ResolveAsync_RuleWithCompleteTier1_OverridesSettingsEntirely()
    {
        SeedSetting(SettingKeys.OvertimeTier1AfterHours, "42");
        SeedSetting(SettingKeys.OvertimeTier1Rate, "0.25");
        var rule = SeedRule(tier1AfterHours: 50m, tier1Rate: 0.1m, basis: OvertimeBasis.Week, rateMode: SurchargeRateMode.FixedPerHour);
        StubContract(new EffectiveContractData { SchedulingRuleId = rule.Id });

        var config = await _sut.ResolveAsync(ClientId, Day);

        config.Basis.ShouldBe(OvertimeBasis.Week);
        config.RateMode.ShouldBe(SurchargeRateMode.FixedPerHour);
        config.Tiers.ShouldHaveSingleItem().ShouldBe(new OvertimeTierConfig(50m, 0.1m, SurchargeType.Overtime1));
    }

    [Test]
    public async Task ResolveAsync_ReferencedRuleWithoutOwnLadder_FallsBackToSettings()
    {
        SeedSetting(SettingKeys.OvertimeTier1AfterHours, "42");
        SeedSetting(SettingKeys.OvertimeTier1Rate, "0.25");
        SeedRule(tier1AfterHours: 60m, tier1Rate: 0.9m);
        var referencedRule = SeedRule(tier1AfterHours: 50m, tier1Rate: null);
        StubContract(new EffectiveContractData { SchedulingRuleId = referencedRule.Id });

        var config = await _sut.ResolveAsync(ClientId, Day);

        config.Tiers.ShouldHaveSingleItem().ShouldBe(new OvertimeTierConfig(42m, 0.25m, SurchargeType.Overtime1));
    }

    [Test]
    public async Task ResolveAsync_DatedRevision_ReplacesRuleLadderFromItsValidFrom()
    {
        var rule = SeedRule(tier1AfterHours: 50m, tier1Rate: 0.1m);
        SeedRevision(rule.Id, validFrom: new DateOnly(2026, 7, 1), tier1AfterHours: 45m, tier1Rate: 0.3m, basis: OvertimeBasis.Week);
        StubContract(new EffectiveContractData { SchedulingRuleId = rule.Id });

        var beforeRevision = await _sut.ResolveAsync(ClientId, new DateOnly(2026, 6, 15));
        var afterRevision = await _sut.ResolveAsync(ClientId, Day);

        beforeRevision.Tiers.ShouldHaveSingleItem().ShouldBe(new OvertimeTierConfig(50m, 0.1m, SurchargeType.Overtime1));
        afterRevision.Tiers.ShouldHaveSingleItem().ShouldBe(new OvertimeTierConfig(45m, 0.3m, SurchargeType.Overtime1));
        afterRevision.Basis.ShouldBe(OvertimeBasis.Week);
    }

    [Test]
    public async Task ResolveAsync_ApplicableRevisionWithoutOvertime_FallsThroughToSettingsNotBaseRule()
    {
        SeedSetting(SettingKeys.OvertimeTier1AfterHours, "42");
        SeedSetting(SettingKeys.OvertimeTier1Rate, "0.25");
        var rule = SeedRule(tier1AfterHours: 50m, tier1Rate: 0.1m);
        SeedRevision(rule.Id, validFrom: new DateOnly(2026, 7, 1), tier1AfterHours: null, tier1Rate: null, nightRate: 0.15m);
        StubContract(new EffectiveContractData { SchedulingRuleId = rule.Id });

        var config = await _sut.ResolveAsync(ClientId, Day);

        config.Tiers.ShouldHaveSingleItem().ShouldBe(new OvertimeTierConfig(42m, 0.25m, SurchargeType.Overtime1));
    }

    [Test]
    public async Task ResolveAsync_NoTierSettings_UsesContractOvertimeThresholdForTier1()
    {
        SeedSetting(SettingKeys.OvertimeTier1Rate, "0.25");
        StubContract(new EffectiveContractData { OvertimeThreshold = 42m });

        var config = await _sut.ResolveAsync(ClientId, Day);

        config.Tiers.ShouldHaveSingleItem().ShouldBe(new OvertimeTierConfig(42m, 0.25m, SurchargeType.Overtime1));
        await _contractDataProvider.Received(1)
            .GetEffectiveContractDataAsync(ClientId, Day, Arg.Any<int?>());
    }

    [Test]
    public async Task ResolveAsync_NoTierSettingsAndZeroContractThreshold_YieldsNoTiers()
    {
        SeedSetting(SettingKeys.OvertimeTier1Rate, "0.25");
        StubContract(new EffectiveContractData { OvertimeThreshold = 0m });

        var config = await _sut.ResolveAsync(ClientId, Day);

        config.Tiers.ShouldBeEmpty();
    }
}
