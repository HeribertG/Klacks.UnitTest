// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests the company-wide monthly target hours override in ClientContractDataProvider: on the
/// MonthlyTargetHours payment interval a row for that calendar month replaces the guaranteed hours
/// and wins even over a scheduling rule, scaled by the contract percent (null means full workload).
/// Any other interval, or a month without a row, resolves exactly as before (regression guard).
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.Associations;

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

[TestFixture]
public class ClientContractDataProviderMonthlyTargetHoursTests
{
    private const decimal ContractGuaranteedHours = 160m;
    private const decimal RuleGuaranteedHours = 150m;
    private const decimal JanuaryTargetHours = 184m;

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
        _sut = new ClientContractDataProvider(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task GetEffectiveContractDataAsync_IntervalMonthlyTargetHoursWithPercent_ScalesMonthValue()
    {
        var clientId = await SeedAsync(PaymentInterval.MonthlyTargetHours, percent: 60m);
        await SeedMonthlyTargetHoursAsync(January.Year, January.Month, JanuaryTargetHours);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, January);

        result.GuaranteedHours.ShouldBe(110.4m);
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_PercentNull_TreatedAsFullWorkload()
    {
        var clientId = await SeedAsync(PaymentInterval.MonthlyTargetHours, percent: null);
        await SeedMonthlyTargetHoursAsync(January.Year, January.Month, JanuaryTargetHours);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, January);

        result.GuaranteedHours.ShouldBe(JanuaryTargetHours);
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_MonthWithoutRow_FallsBackToContractGuaranteedHours()
    {
        var clientId = await SeedAsync(PaymentInterval.MonthlyTargetHours, percent: 60m);
        await SeedMonthlyTargetHoursAsync(January.Year, January.Month, JanuaryTargetHours);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, February);

        result.GuaranteedHours.ShouldBe(ContractGuaranteedHours);
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_IntervalMonthly_RowIgnored()
    {
        var clientId = await SeedAsync(PaymentInterval.Monthly, percent: 60m);
        await SeedMonthlyTargetHoursAsync(January.Year, January.Month, JanuaryTargetHours);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, January);

        result.GuaranteedHours.ShouldBe(ContractGuaranteedHours);
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_SchedulingRuleWithGuaranteedHours_OverrideStillWins()
    {
        var clientId = await SeedAsync(PaymentInterval.MonthlyTargetHours, percent: 60m, withSchedulingRule: true);
        await SeedMonthlyTargetHoursAsync(January.Year, January.Month, JanuaryTargetHours);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, January);

        result.GuaranteedHours.ShouldBe(110.4m);
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_SchedulingRuleAndMonthWithoutRow_RuleStillWinsOverContract()
    {
        var clientId = await SeedAsync(PaymentInterval.MonthlyTargetHours, percent: 60m, withSchedulingRule: true);
        await SeedMonthlyTargetHoursAsync(January.Year, January.Month, JanuaryTargetHours);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, February);

        result.GuaranteedHours.ShouldBe(RuleGuaranteedHours);
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_RowOfAnotherYear_NotApplied()
    {
        var clientId = await SeedAsync(PaymentInterval.MonthlyTargetHours, percent: 60m);
        await SeedMonthlyTargetHoursAsync(January.Year - 1, January.Month, JanuaryTargetHours);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, January);

        result.GuaranteedHours.ShouldBe(ContractGuaranteedHours);
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_SoftDeletedRow_NotApplied()
    {
        var clientId = await SeedAsync(PaymentInterval.MonthlyTargetHours, percent: 60m);
        await SeedMonthlyTargetHoursAsync(January.Year, January.Month, JanuaryTargetHours, isDeleted: true);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, January);

        result.GuaranteedHours.ShouldBe(ContractGuaranteedHours);
    }

    [Test]
    public async Task GetEffectiveContractDataForClientsAsync_MixedIntervals_OnlyTargetHoursContractOverridden()
    {
        var overriddenClientId = await SeedAsync(PaymentInterval.MonthlyTargetHours, percent: 50m);
        var plainClientId = await SeedAsync(PaymentInterval.Monthly, percent: 50m);
        await SeedMonthlyTargetHoursAsync(January.Year, January.Month, JanuaryTargetHours);

        var result = await _sut.GetEffectiveContractDataForClientsAsync(
            new List<Guid> { overriddenClientId, plainClientId }, January);

        result[overriddenClientId].GuaranteedHours.ShouldBe(92m);
        result[plainClientId].GuaranteedHours.ShouldBe(ContractGuaranteedHours);
    }

    private async Task<Guid> SeedAsync(PaymentInterval paymentInterval, decimal? percent, bool withSchedulingRule = false)
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
            GuaranteedHours = ContractGuaranteedHours,
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

    private async Task SeedMonthlyTargetHoursAsync(int year, int month, decimal hours, bool isDeleted = false)
    {
        _context.MonthlyTargetHours.Add(new MonthlyTargetHours
        {
            Id = Guid.NewGuid(),
            Year = year,
            Month = month,
            Hours = hours,
            IsDeleted = isDeleted,
        });
        await _context.SaveChangesAsync();
    }
}
