// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests the two rest minimums (minimum pause hours, minimum rest days) resolved by ClientContractDataProvider:
/// a missing company setting falls back to SchedulingPolicyDefaults, a stored 0 stays 0 ("no requirement",
/// owner rule 2026-11-07), and a 0 on the scheduling rule overrides the company setting.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.Associations;

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
public class ClientContractDataProviderRestMinimumTests
{
    private static readonly DateOnly ReferenceDate = new(2026, 11, 9);

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
    public async Task GetEffectiveContractDataAsync_MissingRestSettings_FallBackToTheDefaults()
    {
        var result = await _sut.GetEffectiveContractDataAsync(Guid.NewGuid(), ReferenceDate);

        result.MinPauseHours.ShouldBe((decimal)SchedulingPolicyDefaults.MinRestHours);
        result.MinRestDays.ShouldBe(SchedulingPolicyDefaults.MinRestDays);
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_RestSettingsZero_StayZero()
    {
        AddSetting(SettingKeys.SchedulingMinPauseHours, "0");
        AddSetting(SettingKeys.SchedulingMinRestDays, "0");
        await _context.SaveChangesAsync();

        var result = await _sut.GetEffectiveContractDataAsync(Guid.NewGuid(), ReferenceDate);

        result.MinPauseHours.ShouldBe(0m);
        result.MinRestDays.ShouldBe(0m);
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_RuleRestZero_OverridesTheCompanySetting()
    {
        AddSetting(SettingKeys.SchedulingMinPauseHours, "11");
        AddSetting(SettingKeys.SchedulingMinRestDays, "2");
        var clientId = SeedClientWithRule(new SchedulingRule
        {
            Id = Guid.NewGuid(),
            Name = "Rule",
            MinPauseHours = 0m,
            MinRestDays = 0m,
        });
        await _context.SaveChangesAsync();

        var result = await _sut.GetEffectiveContractDataAsync(clientId, ReferenceDate);

        result.MinPauseHours.ShouldBe(0m);
        result.MinRestDays.ShouldBe(0m);
    }

    private void AddSetting(string key, string value)
    {
        _context.Settings.Add(new SettingsEntity { Id = Guid.NewGuid(), Type = key, Value = value });
    }

    private Guid SeedClientWithRule(SchedulingRule rule)
    {
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

        return clientId;
    }
}
