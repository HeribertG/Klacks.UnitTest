// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests the daily work frame chain resolved by ClientContractDataProvider: an empty value on the scheduling rule
/// inherits the company setting (the law of the region package), an explicit value wins, and an explicit 0 on the
/// rule overrides the setting with "no frame" (24h minus the minimum rest downstream).
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.Associations;

using System.Globalization;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Associations;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

[TestFixture]
public class ClientContractDataProviderDailySpanTests
{
    private const decimal CountryFrameHours = 14m;
    private static readonly DateOnly ReferenceDate = new(2026, 7, 15);

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
    public async Task GetEffectiveContractDataAsync_RuleWithoutFrame_InheritsTheCompanySetting()
    {
        var clientId = await SeedAsync(ruleMaxDailySpanHours: null);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, ReferenceDate);

        result.MaxDailySpanHours.ShouldBe(CountryFrameHours);
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_RuleFrame_WinsOverTheCompanySetting()
    {
        var clientId = await SeedAsync(ruleMaxDailySpanHours: 13.5m);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, ReferenceDate);

        result.MaxDailySpanHours.ShouldBe(13.5m);
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_RuleFrameZero_OverridesTheCompanySettingWithNoFrame()
    {
        var clientId = await SeedAsync(ruleMaxDailySpanHours: 0m);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, ReferenceDate);

        result.MaxDailySpanHours.ShouldBe(0m);
    }

    private async Task<Guid> SeedAsync(decimal? ruleMaxDailySpanHours)
    {
        _context.Settings.Add(new SettingsEntity
        {
            Id = Guid.NewGuid(),
            Type = SettingKeys.SchedulingMaxDailySpanHours,
            Value = CountryFrameHours.ToString(CultureInfo.InvariantCulture),
        });

        var rule = new SchedulingRule
        {
            Id = Guid.NewGuid(),
            Name = "Rule",
            MaxDailySpanHours = ruleMaxDailySpanHours,
        };
        _context.SchedulingRules.Add(rule);

        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = "Contract",
            PaymentInterval = PaymentInterval.Monthly,
            ValidFrom = new DateTime(2020, 1, 1),
            SchedulingRuleId = rule.Id,
        };
        _context.Contract.Add(contract);

        var clientId = Guid.NewGuid();
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
