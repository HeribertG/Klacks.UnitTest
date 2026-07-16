// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests the dated surcharge-rate revision resolution (validFrom, full-snapshot semantics) in
/// ClientContractDataProvider: the latest revision effective on or before the work date replaces the
/// scheduling rule's base rate columns entirely, a null rate in the applicable revision falls through to
/// contract/settings (never to the base rule or an earlier revision), and an installation without any
/// revision resolves exactly as before (regression guard).
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.Associations;

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Associations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class ClientContractDataProviderRateRevisionTests
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
        _sut = new ClientContractDataProvider(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task GetEffectiveContractDataAsync_NoRevisions_ResolvesToBaseRuleRate()
    {
        var clientId = await SeedAsync(
            ruleNightRate: 0.20m,
            revisions: Array.Empty<(DateOnly, decimal?)>());

        var result = await _sut.GetEffectiveContractDataAsync(clientId, new DateOnly(2027, 6, 1));

        result.NightRate.ShouldBe(0.20m);
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_DateBeforeFirstRevision_UsesBaseRuleRate()
    {
        var clientId = await SeedAsync(
            ruleNightRate: 0.20m,
            revisions: new (DateOnly, decimal?)[] { (new DateOnly(2027, 1, 1), 0.30m) });

        var result = await _sut.GetEffectiveContractDataAsync(clientId, new DateOnly(2026, 12, 31));

        result.NightRate.ShouldBe(0.20m);
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_DateOnRevision_UsesRevisionRate()
    {
        var clientId = await SeedAsync(
            ruleNightRate: 0.20m,
            revisions: new (DateOnly, decimal?)[] { (new DateOnly(2027, 1, 1), 0.30m) });

        var result = await _sut.GetEffectiveContractDataAsync(clientId, new DateOnly(2027, 1, 1));

        result.NightRate.ShouldBe(0.30m);
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_MultipleRevisions_UsesLatestOnOrBeforeDate()
    {
        var clientId = await SeedAsync(
            ruleNightRate: 0.20m,
            revisions: new (DateOnly, decimal?)[]
            {
                (new DateOnly(2027, 1, 1), 0.30m),
                (new DateOnly(2028, 1, 1), 0.35m),
            });

        var early = await _sut.GetEffectiveContractDataAsync(clientId, new DateOnly(2027, 6, 1));
        var late = await _sut.GetEffectiveContractDataAsync(clientId, new DateOnly(2028, 5, 1));

        early.NightRate.ShouldBe(0.30m);
        late.NightRate.ShouldBe(0.35m);
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_RevisionWithNullRate_FallsToSettingsNotBaseRuleOrEarlierRevision()
    {
        _context.Settings.Add(new Settings { Id = Guid.NewGuid(), Type = SettingKeys.NightRate, Value = "0.10" });
        await _context.SaveChangesAsync();

        var clientId = await SeedAsync(
            ruleNightRate: 0.20m,
            revisions: new (DateOnly, decimal?)[]
            {
                (new DateOnly(2027, 1, 1), 0.30m),
                (new DateOnly(2028, 1, 1), null),
            });

        var result = await _sut.GetEffectiveContractDataAsync(clientId, new DateOnly(2028, 6, 1));

        result.NightRate.ShouldBe(0.10m);
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_RevisionSetsAllFiveRates_EachResolvesIndependently()
    {
        var clientId = Guid.NewGuid();

        var rule = new SchedulingRule
        {
            Id = Guid.NewGuid(),
            Name = "Rule",
            NightRate = 0.20m,
            HolidayRate = 0.20m,
            WE1Rate = 0.20m,
            WE2Rate = 0.20m,
            WE3Rate = 0.20m,
        };
        _context.SchedulingRules.Add(rule);
        _context.SchedulingRuleRateRevisions.Add(new SchedulingRuleRateRevision
        {
            Id = Guid.NewGuid(),
            SchedulingRuleId = rule.Id,
            ValidFrom = new DateOnly(2027, 1, 1),
            NightRate = 0.31m,
            HolidayRate = 0.32m,
            WE1Rate = 0.33m,
            WE2Rate = 0.34m,
            WE3Rate = 0.35m,
        });
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = "Contract",
            PaymentInterval = PaymentInterval.Monthly,
            ValidFrom = new DateTime(2020, 1, 1),
            SchedulingRuleId = rule.Id,
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

        var result = await _sut.GetEffectiveContractDataAsync(clientId, new DateOnly(2027, 6, 1));

        result.NightRate.ShouldBe(0.31m);
        result.HolidayRate.ShouldBe(0.32m);
        result.WE1Rate.ShouldBe(0.33m);
        result.WE2Rate.ShouldBe(0.34m);
        result.WE3Rate.ShouldBe(0.35m);
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_ContractWithoutSchedulingRule_RevisionsForOtherRuleIgnored_UsesContractRate()
    {
        var clientId = Guid.NewGuid();

        var otherRule = new SchedulingRule { Id = Guid.NewGuid(), Name = "OtherRule", NightRate = 0.20m };
        _context.SchedulingRules.Add(otherRule);
        _context.SchedulingRuleRateRevisions.Add(new SchedulingRuleRateRevision
        {
            Id = Guid.NewGuid(),
            SchedulingRuleId = otherRule.Id,
            ValidFrom = new DateOnly(2027, 1, 1),
            NightRate = 0.99m,
        });

        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = "Contract",
            PaymentInterval = PaymentInterval.Monthly,
            ValidFrom = new DateTime(2020, 1, 1),
            SchedulingRuleId = null,
            NightRate = 0.45m,
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

        var result = await _sut.GetEffectiveContractDataAsync(clientId, new DateOnly(2027, 6, 1));

        result.NightRate.ShouldBe(0.45m);
        result.SchedulingRuleId.ShouldBeNull();
    }

    private async Task<Guid> SeedAsync(decimal ruleNightRate, IReadOnlyList<(DateOnly ValidFrom, decimal? NightRate)> revisions)
    {
        var clientId = Guid.NewGuid();

        var rule = new SchedulingRule { Id = Guid.NewGuid(), Name = "Rule", NightRate = ruleNightRate };
        _context.SchedulingRules.Add(rule);

        foreach (var (validFrom, nightRate) in revisions)
        {
            _context.SchedulingRuleRateRevisions.Add(new SchedulingRuleRateRevision
            {
                Id = Guid.NewGuid(),
                SchedulingRuleId = rule.Id,
                ValidFrom = validFrom,
                NightRate = nightRate,
            });
        }

        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = "Contract",
            PaymentInterval = PaymentInterval.Monthly,
            ValidFrom = new DateTime(2020, 1, 1),
            SchedulingRuleId = rule.Id,
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
