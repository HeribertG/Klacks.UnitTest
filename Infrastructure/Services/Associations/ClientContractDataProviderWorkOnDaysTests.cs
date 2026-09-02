// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests the WorkOnMonday..WorkOnSunday/PerformsShiftWork fallback chain (scheduling rule -&gt; contract)
/// resolved by ClientContractDataProvider. Regression coverage for a bug where these 8 fields read only
/// contract.X, ignoring a bound SchedulingRule and silently defeating PerformsShiftWork=true on the rule.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.Associations;

using Klacks.Api.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Associations;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class ClientContractDataProviderWorkOnDaysTests
{
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
        _sut = new ClientContractDataProvider(_context, NullLogger<ClientContractDataProvider>.Instance, new SettingsChangeVersion());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task GetEffectiveContractDataAsync_RulePerformsShiftWorkTrue_OverridesFalseContract()
    {
        var clientId = await SeedActiveContractAsync(
            contractPerformsShiftWork: false, rulePerformsShiftWork: true, ruleWorkOnSaturday: null, contractWorkOnSaturday: false);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, new DateOnly(2026, 7, 15));

        result.PerformsShiftWork.ShouldBeTrue();
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_RulePerformsShiftWorkNull_FallsBackToContractValue()
    {
        var clientId = await SeedActiveContractAsync(
            contractPerformsShiftWork: false, rulePerformsShiftWork: null, ruleWorkOnSaturday: null, contractWorkOnSaturday: false);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, new DateOnly(2026, 7, 15));

        result.PerformsShiftWork.ShouldBeFalse();
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_RuleWorkOnSaturdayTrue_OverridesFalseContract()
    {
        var clientId = await SeedActiveContractAsync(
            contractPerformsShiftWork: false, rulePerformsShiftWork: null, ruleWorkOnSaturday: true, contractWorkOnSaturday: false);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, new DateOnly(2026, 7, 15));

        result.WorkOnSaturday.ShouldBeTrue();
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_RuleWorkOnSaturdayFalse_OverridesTrueContract()
    {
        var clientId = await SeedActiveContractAsync(
            contractPerformsShiftWork: false, rulePerformsShiftWork: null, ruleWorkOnSaturday: false, contractWorkOnSaturday: true);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, new DateOnly(2026, 7, 15));

        result.WorkOnSaturday.ShouldBeFalse();
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_NoSchedulingRule_UsesContractWorkOnSaturday()
    {
        var clientId = await SeedActiveContractAsync(
            contractPerformsShiftWork: false, rulePerformsShiftWork: null, ruleWorkOnSaturday: null, contractWorkOnSaturday: true, includeRule: false);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, new DateOnly(2026, 7, 15));

        result.WorkOnSaturday.ShouldBeTrue();
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_RuleBound_GuaranteedHoursStillFollowsRuleThenContractChain()
    {
        var clientId = await SeedActiveContractAsync(
            contractPerformsShiftWork: false, rulePerformsShiftWork: true, ruleWorkOnSaturday: null, contractWorkOnSaturday: false,
            ruleGuaranteedHours: 42m, contractGuaranteedHours: 10m);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, new DateOnly(2026, 7, 15));

        result.GuaranteedHours.ShouldBe(42m);
    }

    private async Task<Guid> SeedActiveContractAsync(
        bool contractPerformsShiftWork,
        bool? rulePerformsShiftWork,
        bool? ruleWorkOnSaturday,
        bool contractWorkOnSaturday,
        bool includeRule = true,
        decimal? ruleGuaranteedHours = null,
        decimal? contractGuaranteedHours = null)
    {
        var clientId = Guid.NewGuid();

        SchedulingRule? rule = null;
        if (includeRule)
        {
            rule = new SchedulingRule
            {
                Id = Guid.NewGuid(),
                Name = "Rule",
                PerformsShiftWork = rulePerformsShiftWork,
                WorkOnSaturday = ruleWorkOnSaturday,
                GuaranteedHours = ruleGuaranteedHours,
            };
            _context.SchedulingRules.Add(rule);
        }

        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = "Contract",
            PerformsShiftWork = contractPerformsShiftWork,
            WorkOnSaturday = contractWorkOnSaturday,
            GuaranteedHours = contractGuaranteedHours,
            PaymentInterval = PaymentInterval.Monthly,
            ValidFrom = new DateTime(2020, 1, 1),
            SchedulingRuleId = rule?.Id,
        };
        _context.Contract.Add(contract);

        _context.ClientContract.Add(new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ContractId = contract.Id,
            FromDate = new DateOnly(2020, 1, 1),
            UntilDate = null,
            IsActive = true,
        });

        await _context.SaveChangesAsync();
        return clientId;
    }
}
