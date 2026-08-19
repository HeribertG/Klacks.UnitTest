// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests the "empty field inherits" rule in ClientContractDataProvider: a contract with
/// GuaranteedHours = null inherits the company-wide monthly value (or the settings default when the
/// month has no row), scaled by the contract percent; a scheduling rule's guaranteed hours are
/// deliberately ignored while inheriting. An explicit contract value (including 0 for on-call
/// contracts) is never scaled and keeps the original rule -> contract chain. WorkloadPercent
/// carries the contract percent only for contracts deriving from the company value.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.Associations;

using Klacks.Api.Domain.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Associations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

[TestFixture]
public class ClientContractDataProviderInheritedGuaranteedHoursTests
{
    private const decimal ExplicitContractHours = 160m;
    private const decimal RuleGuaranteedHours = 150m;
    private const decimal JanuaryTargetHours = 184m;
    private const decimal SettingsGuaranteedHours = 170m;

    private static readonly DateOnly January = new(2026, 1, 1);
    private static readonly DateOnly February = new(2026, 2, 1);

    private DataBaseContext _context = null!;
    private ClientContractDataProvider _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, httpContextAccessor);
        _sut = new ClientContractDataProvider(_context, NullLogger<ClientContractDataProvider>.Instance);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task Inheriting_MonthRowExists_TakesRowScaledByPercent()
    {
        await SeedGuaranteedHoursSettingAsync();
        var clientId = await SeedAsync(guaranteedHours: null, percent: 60m);
        await SeedMonthlyTargetHoursAsync(January.Year, January.Month, JanuaryTargetHours);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, January);

        result.GuaranteedHours.ShouldBe(110.4m);
    }

    [Test]
    public async Task Inheriting_MonthRowExists_NoPercent_TakesRawRow()
    {
        await SeedGuaranteedHoursSettingAsync();
        var clientId = await SeedAsync(guaranteedHours: null, percent: null);
        await SeedMonthlyTargetHoursAsync(January.Year, January.Month, JanuaryTargetHours);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, January);

        result.GuaranteedHours.ShouldBe(JanuaryTargetHours);
    }

    [Test]
    public async Task Inheriting_NoMonthRow_SettingsFallbackIsScaledByPercent()
    {
        await SeedGuaranteedHoursSettingAsync();
        var clientId = await SeedAsync(guaranteedHours: null, percent: 80m);
        await SeedMonthlyTargetHoursAsync(January.Year, January.Month, JanuaryTargetHours);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, February);

        result.GuaranteedHours.ShouldBe(136m);
    }

    [Test]
    public async Task Inheriting_NoMonthRow_NoPercent_TakesSettings()
    {
        await SeedGuaranteedHoursSettingAsync();
        var clientId = await SeedAsync(guaranteedHours: null, percent: null);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, January);

        result.GuaranteedHours.ShouldBe(SettingsGuaranteedHours);
    }

    [Test]
    public async Task ExplicitValue_WithPercent_IsNeverScaled()
    {
        await SeedGuaranteedHoursSettingAsync();
        var clientId = await SeedAsync(guaranteedHours: ExplicitContractHours, percent: 60m);
        await SeedMonthlyTargetHoursAsync(January.Year, January.Month, JanuaryTargetHours);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, January);

        result.GuaranteedHours.ShouldBe(ExplicitContractHours);
    }

    [Test]
    public async Task ExplicitZero_OnCallContract_StaysZero()
    {
        await SeedGuaranteedHoursSettingAsync();
        var clientId = await SeedAsync(guaranteedHours: 0m, percent: null);
        await SeedMonthlyTargetHoursAsync(January.Year, January.Month, JanuaryTargetHours);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, January);

        result.GuaranteedHours.ShouldBe(0m);
    }

    [Test]
    public async Task Inheriting_RuleWithGuaranteedHours_RuleIsIgnored()
    {
        await SeedGuaranteedHoursSettingAsync();
        var clientId = await SeedAsync(guaranteedHours: null, percent: null, withSchedulingRule: true);
        await SeedMonthlyTargetHoursAsync(January.Year, January.Month, JanuaryTargetHours);

        var withRow = await _sut.GetEffectiveContractDataAsync(clientId, January);
        var withoutRow = await _sut.GetEffectiveContractDataAsync(clientId, February);

        withRow.GuaranteedHours.ShouldBe(JanuaryTargetHours);
        withoutRow.GuaranteedHours.ShouldBe(SettingsGuaranteedHours);
    }

    [Test]
    public async Task ExplicitValue_RuleStillWinsOverContract()
    {
        await SeedGuaranteedHoursSettingAsync();
        var clientId = await SeedAsync(guaranteedHours: ExplicitContractHours, percent: null, withSchedulingRule: true);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, January);

        result.GuaranteedHours.ShouldBe(RuleGuaranteedHours);
    }

    [Test]
    public async Task Interval4_InheritingWithoutRow_SettingsFallbackIsScaled()
    {
        await SeedGuaranteedHoursSettingAsync();
        var clientId = await SeedAsync(
            guaranteedHours: null, percent: 50m, paymentInterval: PaymentInterval.MonthlyTargetHours);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, January);

        result.GuaranteedHours.ShouldBe(85m);
    }

    [Test]
    public async Task Interval4_ExplicitValue_MonthRowStillWinsScaled()
    {
        await SeedGuaranteedHoursSettingAsync();
        var clientId = await SeedAsync(
            guaranteedHours: ExplicitContractHours, percent: 50m, paymentInterval: PaymentInterval.MonthlyTargetHours);
        await SeedMonthlyTargetHoursAsync(January.Year, January.Month, JanuaryTargetHours);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, January);

        result.GuaranteedHours.ShouldBe(92m);
    }

    [Test]
    public async Task WorkloadPercent_InheritingContract_CarriesContractPercent()
    {
        await SeedGuaranteedHoursSettingAsync();
        var clientId = await SeedAsync(guaranteedHours: null, percent: 60m);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, January);

        result.WorkloadPercent.ShouldBe(60m);
    }

    [Test]
    public async Task WorkloadPercent_Interval4Contract_CarriesContractPercent()
    {
        await SeedGuaranteedHoursSettingAsync();
        var clientId = await SeedAsync(
            guaranteedHours: ExplicitContractHours, percent: 40m, paymentInterval: PaymentInterval.MonthlyTargetHours);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, January);

        result.WorkloadPercent.ShouldBe(40m);
    }

    [Test]
    public async Task WorkloadPercent_ExplicitContract_DerivedFromGuaranteedOverFullTime()
    {
        await SeedGuaranteedHoursSettingAsync();
        var clientId = await SeedAsync(guaranteedHours: ExplicitContractHours, percent: null, fullTime: 180m);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, January);

        result.WorkloadPercent.ShouldBe(160m / 180m * MonthlyTargetHoursConstants.FullWorkloadPercent);
    }

    [Test]
    public async Task WorkloadPercent_ExplicitContractWithStalePercent_PercentIgnoredRatioWins()
    {
        await SeedGuaranteedHoursSettingAsync();
        var clientId = await SeedAsync(guaranteedHours: ExplicitContractHours, percent: 60m, fullTime: 180m);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, January);

        result.WorkloadPercent.ShouldBe(160m / 180m * MonthlyTargetHoursConstants.FullWorkloadPercent);
    }

    [Test]
    public async Task WorkloadPercent_ModestOverFullTimeContract_RatioAboveHundredAllowed()
    {
        await SeedGuaranteedHoursSettingAsync();
        var clientId = await SeedAsync(guaranteedHours: 198m, percent: null, fullTime: 180m);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, January);

        result.WorkloadPercent.ShouldBe(110m);
    }

    [Test]
    public async Task WorkloadPercent_ImplausibleRatio_FallsBackToFullWorkload()
    {
        await SeedGuaranteedHoursSettingAsync();
        var clientId = await SeedAsync(guaranteedHours: 172.2m, percent: null, fullTime: 45m);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, January);

        result.WorkloadPercent.ShouldBe(MonthlyTargetHoursConstants.FullWorkloadPercent);
    }

    [Test]
    public async Task WorkloadPercent_NoFullTimeBasis_FallsBackToFullWorkload()
    {
        await SeedGuaranteedHoursSettingAsync();
        var clientId = await SeedAsync(guaranteedHours: ExplicitContractHours, percent: null, fullTime: null);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, January);

        result.WorkloadPercent.ShouldBe(MonthlyTargetHoursConstants.FullWorkloadPercent);
    }

    [Test]
    public async Task WorkloadPercent_ZeroHoursOnCallContract_IsZero()
    {
        await SeedGuaranteedHoursSettingAsync();
        var clientId = await SeedAsync(guaranteedHours: 0m, percent: null, fullTime: 180m);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, January);

        result.WorkloadPercent.ShouldBe(0m);
    }

    [Test]
    public async Task WorkloadPercent_ContractlessClient_IsFullWorkload()
    {
        await SeedGuaranteedHoursSettingAsync();

        var result = await _sut.GetEffectiveContractDataAsync(Guid.NewGuid(), January);

        result.WorkloadPercent.ShouldBe(MonthlyTargetHoursConstants.FullWorkloadPercent);
    }

    [Test]
    public async Task BatchPath_SingleInheritingMonthlyContract_GateOpensForMonthRow()
    {
        await SeedGuaranteedHoursSettingAsync();
        var clientId = await SeedAsync(guaranteedHours: null, percent: null);
        await SeedMonthlyTargetHoursAsync(January.Year, January.Month, JanuaryTargetHours);

        var result = await _sut.GetEffectiveContractDataForClientsAsync(new List<Guid> { clientId }, January);

        result[clientId].GuaranteedHours.ShouldBe(JanuaryTargetHours);
    }

    [Test]
    public async Task BatchPath_MixedExplicitAndInheriting_OnlyInheritingTakesRow()
    {
        await SeedGuaranteedHoursSettingAsync();
        var inheritingClientId = await SeedAsync(guaranteedHours: null, percent: null);
        var explicitClientId = await SeedAsync(guaranteedHours: ExplicitContractHours, percent: null);
        await SeedMonthlyTargetHoursAsync(January.Year, January.Month, JanuaryTargetHours);

        var result = await _sut.GetEffectiveContractDataForClientsAsync(
            new List<Guid> { inheritingClientId, explicitClientId }, January);

        result[inheritingClientId].GuaranteedHours.ShouldBe(JanuaryTargetHours);
        result[explicitClientId].GuaranteedHours.ShouldBe(ExplicitContractHours);
    }

    [Test]
    public async Task RangePath_InheritingContract_RowAppliedPerDayAndSettingsScaledOutsideMonth()
    {
        await SeedGuaranteedHoursSettingAsync();
        var clientId = await SeedAsync(guaranteedHours: null, percent: 50m);
        await SeedMonthlyTargetHoursAsync(January.Year, January.Month, JanuaryTargetHours);

        var lastOfJanuary = new DateOnly(January.Year, January.Month, 31);
        var result = await _sut.GetEffectiveContractDataForClientsRangeAsync(
            new List<Guid> { clientId }, lastOfJanuary, February);

        result[lastOfJanuary][clientId].GuaranteedHours.ShouldBe(92m);
        result[February][clientId].GuaranteedHours.ShouldBe(85m);
    }

    private async Task<Guid> SeedAsync(
        decimal? guaranteedHours,
        decimal? percent,
        PaymentInterval paymentInterval = PaymentInterval.Monthly,
        bool withSchedulingRule = false,
        decimal? fullTime = null)
    {
        var clientId = Guid.NewGuid();

        Guid? ruleId = null;
        if (withSchedulingRule)
        {
            var rule = new SchedulingRule
            {
                Id = Guid.NewGuid(),
                Name = "Rule",
                GuaranteedHours = RuleGuaranteedHours,
            };
            _context.SchedulingRules.Add(rule);
            ruleId = rule.Id;
        }

        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = "Contract",
            PaymentInterval = paymentInterval,
            Percent = percent,
            GuaranteedHours = guaranteedHours,
            FullTime = fullTime,
            ValidFrom = new DateTime(2020, 1, 1),
            SchedulingRuleId = ruleId,
        };
        _context.Contract.Add(contract);
        _context.ClientContract.Add(new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ContractId = contract.Id,
            FromDate = new DateOnly(2020, 1, 1),
            IsActive = true,
        });
        await _context.SaveChangesAsync();

        return clientId;
    }

    private async Task SeedGuaranteedHoursSettingAsync()
    {
        _context.Settings.Add(new SettingsEntity
        {
            Id = Guid.NewGuid(),
            Type = SettingKeys.GuaranteedHours,
            Value = SettingsGuaranteedHours.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        await _context.SaveChangesAsync();
    }

    private async Task SeedMonthlyTargetHoursAsync(int year, int month, decimal hours)
    {
        _context.MonthlyTargetHours.Add(new MonthlyTargetHours
        {
            Id = Guid.NewGuid(),
            Year = year,
            Month = month,
            Hours = hours,
        });
        await _context.SaveChangesAsync();
    }
}
