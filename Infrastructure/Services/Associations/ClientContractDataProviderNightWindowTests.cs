// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests the night surcharge window fallback chain (scheduling rule -&gt; contract -&gt; settings -&gt; hard
/// default) resolved by ClientContractDataProvider, mirroring the existing chain already proven for
/// NightRate/GuaranteedHours/etc.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.Associations;

using Klacks.Api.Domain.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Associations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class ClientContractDataProviderNightWindowTests
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
        _sut = new ClientContractDataProvider(_context, NullLogger<ClientContractDataProvider>.Instance);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task GetEffectiveContractDataAsync_NoContractNoSettings_FallsBackToHardDefault()
    {
        var clientId = Guid.NewGuid();

        var result = await _sut.GetEffectiveContractDataAsync(clientId, new DateOnly(2026, 7, 15));

        result.NightStart.ShouldBe(SurchargeDefaults.NightStart);
        result.NightEnd.ShouldBe(SurchargeDefaults.NightEnd);
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_OnlySettingsConfigured_UsesSettingsValue()
    {
        var clientId = Guid.NewGuid();
        await SeedSettingsAsync("22:00", "04:00");

        var result = await _sut.GetEffectiveContractDataAsync(clientId, new DateOnly(2026, 7, 15));

        result.NightStart.ShouldBe("22:00");
        result.NightEnd.ShouldBe("04:00");
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_ContractOverridesSettings_UsesContractValue()
    {
        await SeedSettingsAsync("22:00", "04:00");
        var clientId = await SeedActiveContractAsync(contractNightStart: "21:00", contractNightEnd: "05:00", ruleNightStart: null, ruleNightEnd: null);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, new DateOnly(2026, 7, 15));

        result.NightStart.ShouldBe("21:00");
        result.NightEnd.ShouldBe("05:00");
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_SchedulingRuleOverridesContractAndSettings_UsesRuleValue()
    {
        await SeedSettingsAsync("22:00", "04:00");
        var clientId = await SeedActiveContractAsync(contractNightStart: "21:00", contractNightEnd: "05:00", ruleNightStart: "20:00", ruleNightEnd: "06:00");

        var result = await _sut.GetEffectiveContractDataAsync(clientId, new DateOnly(2026, 7, 15));

        result.NightStart.ShouldBe("20:00");
        result.NightEnd.ShouldBe("06:00");
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_EditedNightWindowSettings_YieldDifferentEffectiveWindow()
    {
        // The provider is scoped and resolves the global settings once per scope, because the
        // recalculation pipeline asks for contract data once per Work. A settings change is therefore
        // picked up by the NEXT scope - which is what production does: the settings handler only queues
        // a recalculation, and ThoroughRecalculationBackgroundService opens a fresh scope per request.
        var clientId = Guid.NewGuid();
        await SeedSettingsAsync("22:00", "04:00");
        var before = await _sut.GetEffectiveContractDataAsync(clientId, new DateOnly(2026, 7, 15));

        await UpdateSettingAsync(SettingKeys.SurchargeNightStart, "21:00");
        await UpdateSettingAsync(SettingKeys.SurchargeNightEnd, "05:00");
        var after = await NextScopeProvider().GetEffectiveContractDataAsync(clientId, new DateOnly(2026, 7, 15));

        before.NightStart.ShouldBe("22:00");
        before.NightEnd.ShouldBe("04:00");
        after.NightStart.ShouldBe("21:00");
        after.NightEnd.ShouldBe("05:00");
    }

    [Test]
    public async Task GetEffectiveContractDataAsync_SettingsEditedWithinTheSameScope_KeepsTheResolvedSnapshot()
    {
        var clientId = Guid.NewGuid();
        await SeedSettingsAsync("22:00", "04:00");
        await _sut.GetEffectiveContractDataAsync(clientId, new DateOnly(2026, 7, 15));

        await UpdateSettingAsync(SettingKeys.SurchargeNightStart, "21:00");
        var again = await _sut.GetEffectiveContractDataAsync(clientId, new DateOnly(2026, 7, 15));

        // Not a stale read to be fixed: no code path writes settings and resolves contract data in the
        // same scope, and re-reading them per Work is the single largest cost of a recalculation run.
        again.NightStart.ShouldBe("22:00");
    }

    /// <summary>A provider as the DI container would hand it out for the next request.</summary>
    private ClientContractDataProvider NextScopeProvider() => new(_context, NullLogger<ClientContractDataProvider>.Instance);

    private async Task SeedSettingsAsync(string nightStart, string nightEnd)
    {
        _context.Settings.Add(new Settings { Id = Guid.NewGuid(), Type = SettingKeys.SurchargeNightStart, Value = nightStart });
        _context.Settings.Add(new Settings { Id = Guid.NewGuid(), Type = SettingKeys.SurchargeNightEnd, Value = nightEnd });
        await _context.SaveChangesAsync();
    }

    private async Task UpdateSettingAsync(string type, string value)
    {
        var row = _context.Settings.Single(s => s.Type == type);
        row.Value = value;
        await _context.SaveChangesAsync();
    }

    private async Task<Guid> SeedActiveContractAsync(
        string? contractNightStart, string? contractNightEnd, string? ruleNightStart, string? ruleNightEnd)
    {
        var clientId = Guid.NewGuid();

        SchedulingRule? rule = null;
        if (ruleNightStart != null || ruleNightEnd != null)
        {
            rule = new SchedulingRule { Id = Guid.NewGuid(), Name = "Rule", NightStart = ruleNightStart, NightEnd = ruleNightEnd };
            _context.SchedulingRules.Add(rule);
        }

        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = "Contract",
            NightStart = contractNightStart,
            NightEnd = contractNightEnd,
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
