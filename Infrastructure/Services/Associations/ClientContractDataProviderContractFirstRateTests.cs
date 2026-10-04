// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Resolution table for the five surcharge time-credit rates (night, holiday, WE1-WE3) and the tri-state
/// PerformsShiftWork flag (owner decision 2026-10-04): an explicit contract value (also 0) wins, null means
/// "standard" = the scheduling rule (a dated rate revision replaces the rule's rate columns as a full
/// snapshot), then the installation settings. Effective PerformsShiftWork = contract ?? rule ?? settings;
/// when it resolves to false, all five contract rates are ignored. Every row is checked on the single-day
/// path and on the range path, and every rate field carries a distinct value so a field mix-up fails.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.Associations;

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Associations;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class ClientContractDataProviderContractFirstRateTests
{
    private const decimal SettingsRate = 0.1m;
    private const decimal FieldOffset = 0.001m;
    private const int RateFieldCount = 5;
    private static readonly DateOnly WorkDate = new(2026, 7, 15);
    private static readonly DateOnly RevisionValidFrom = new(2026, 1, 1);

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

    public sealed record Row(
        string Name,
        decimal? ContractRate,
        bool HasRule,
        decimal? RuleRate,
        bool HasRevision,
        decimal? RevisionRate,
        bool? ContractShiftWork,
        bool? RuleShiftWork,
        string? SettingsShiftWork,
        decimal ExpectedRate,
        bool ExpectedShiftWork)
    {
        public override string ToString() => Name;
    }

    private static IEnumerable<Row> Rows()
    {
        yield return new Row("PSW true, no contract rate, no rule -> settings", null, false, null, false, null, true, null, null, SettingsRate, true);
        yield return new Row("PSW true, contract 0, no rule -> 0", 0m, false, null, false, null, true, null, null, 0m, true);
        yield return new Row("PSW true, contract 0.2, no rule -> contract", 0.2m, false, null, false, null, true, null, null, 0.2m, true);
        yield return new Row("PSW true, no contract rate, rule 0.3 -> rule", null, true, 0.3m, false, null, true, null, null, 0.3m, true);
        yield return new Row("PSW true, contract 0, rule 0.3 -> explicit 0 wins", 0m, true, 0.3m, false, null, true, null, null, 0m, true);
        yield return new Row("PSW true, contract 0.2, rule 0.3 -> contract", 0.2m, true, 0.3m, false, null, true, null, null, 0.2m, true);
        yield return new Row("PSW true, no contract rate, rule 0.3, revision 0.4 -> revision", null, true, 0.3m, true, 0.4m, true, null, null, 0.4m, true);
        yield return new Row("PSW true, contract 0.2, rule 0.3, revision 0.4 -> contract", 0.2m, true, 0.3m, true, 0.4m, true, null, null, 0.2m, true);
        yield return new Row("PSW true, no contract rate, rule 0.3, revision null -> settings", null, true, 0.3m, true, null, true, null, null, SettingsRate, true);
        yield return new Row("PSW true, contract 0, rule without rate -> 0", 0m, true, null, false, null, true, null, null, 0m, true);
        yield return new Row("PSW true, no contract rate, rule without rate -> settings", null, true, null, false, null, true, null, null, SettingsRate, true);

        yield return new Row("PSW false, contract 0.2, no rule -> settings", 0.2m, false, null, false, null, false, null, null, SettingsRate, false);
        yield return new Row("PSW false, contract 0, no rule -> settings", 0m, false, null, false, null, false, null, null, SettingsRate, false);
        yield return new Row("PSW false, contract 0.2, rule 0.3 -> rule", 0.2m, true, 0.3m, false, null, false, null, null, 0.3m, false);
        yield return new Row("PSW false, contract 0.2, rule 0.3, revision 0.4 -> revision", 0.2m, true, 0.3m, true, 0.4m, false, null, null, 0.4m, false);

        yield return new Row("PSW null, rule PSW false, contract 0.2, rule 0.3 -> rule", 0.2m, true, 0.3m, false, null, null, false, null, 0.3m, false);
        yield return new Row("PSW null, rule PSW true, contract 0.2, rule 0.3 -> contract", 0.2m, true, 0.3m, false, null, null, true, null, 0.2m, true);
        yield return new Row("PSW null, rule PSW null, settings PSW false, contract 0.2 -> settings", 0.2m, true, null, false, null, null, null, "false", SettingsRate, false);
        yield return new Row("PSW null, no rule, settings PSW false, contract 0.2 -> settings", 0.2m, false, null, false, null, null, null, "false", SettingsRate, false);
        yield return new Row("PSW null, no rule, settings PSW absent, contract 0.2 -> contract", 0.2m, false, null, false, null, null, null, null, 0.2m, true);
        yield return new Row("PSW null, no rule, settings PSW true, contract 0.2 -> contract", 0.2m, false, null, false, null, null, null, "true", 0.2m, true);
        yield return new Row("PSW null, no rule, settings PSW false, no contract rate -> settings", null, false, null, false, null, null, null, "false", SettingsRate, false);

        yield return new Row("contract PSW true beats rule PSW false", 0.2m, true, 0.3m, false, null, true, false, "false", 0.2m, true);
        yield return new Row("contract PSW false beats rule PSW true", 0.2m, true, 0.3m, false, null, false, true, "true", 0.3m, false);
    }

    [TestCaseSource(nameof(Rows))]
    public async Task ResolvesRatesAndShiftWork_SingleDayPath(Row row)
    {
        var clientId = await SeedAsync(row);

        var result = await _sut.GetEffectiveContractDataAsync(clientId, WorkDate);

        AssertRow(result, row);
    }

    [TestCaseSource(nameof(Rows))]
    public async Task ResolvesRatesAndShiftWork_RangePath(Row row)
    {
        var clientId = await SeedAsync(row);

        var range = await _sut.GetEffectiveContractDataForClientsRangeAsync([clientId], WorkDate, WorkDate);

        AssertRow(range[WorkDate][clientId], row);
    }

    [Test]
    public async Task ClientWithoutContract_UsesSettingsRatesAndShiftWorkDefault()
    {
        await SeedSettingsAsync("false");

        var result = await _sut.GetEffectiveContractDataAsync(Guid.NewGuid(), WorkDate);

        result.HasActiveContract.ShouldBeFalse();
        AssertRates(result, SettingsRate);
        result.PerformsShiftWork.ShouldBeFalse();
    }

    private static void AssertRow(EffectiveContractData result, Row row)
    {
        result.HasActiveContract.ShouldBeTrue();
        AssertRates(result, row.ExpectedRate);
        result.PerformsShiftWork.ShouldBe(row.ExpectedShiftWork);
    }

    private static void AssertRates(EffectiveContractData result, decimal expected)
    {
        result.NightRate.ShouldBe(FieldValue(expected, 0), "NightRate");
        result.HolidayRate.ShouldBe(FieldValue(expected, 1), "HolidayRate");
        result.WE1Rate.ShouldBe(FieldValue(expected, 2), "WE1Rate");
        result.WE2Rate.ShouldBe(FieldValue(expected, 3), "WE2Rate");
        result.WE3Rate.ShouldBe(FieldValue(expected, 4), "WE3Rate");
    }

    private static decimal FieldValue(decimal value, int fieldIndex) =>
        value == decimal.Zero ? decimal.Zero : value + fieldIndex * FieldOffset;

    private static decimal? FieldValue(decimal? value, int fieldIndex) =>
        value.HasValue ? FieldValue(value.Value, fieldIndex) : null;

    private async Task SeedSettingsAsync(string? settingsShiftWork)
    {
        var rateKeys = new[] { SettingKeys.NightRate, SettingKeys.HolidayRate, SettingKeys.WE1Rate, SettingKeys.WE2Rate, SettingKeys.WE3Rate };
        rateKeys.Length.ShouldBe(RateFieldCount);
        for (var i = 0; i < rateKeys.Length; i++)
        {
            _context.Settings.Add(new Settings
            {
                Id = Guid.NewGuid(),
                Type = rateKeys[i],
                Value = FieldValue(SettingsRate, i).ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
        }

        if (settingsShiftWork != null)
        {
            _context.Settings.Add(new Settings { Id = Guid.NewGuid(), Type = SettingKeys.SchedulingDefaultPerformsShiftWork, Value = settingsShiftWork });
        }

        await _context.SaveChangesAsync();
    }

    private async Task<Guid> SeedAsync(Row row)
    {
        await SeedSettingsAsync(row.SettingsShiftWork);

        SchedulingRule? rule = null;
        if (row.HasRule)
        {
            rule = new SchedulingRule
            {
                Id = Guid.NewGuid(),
                Name = "Rule",
                NightRate = FieldValue(row.RuleRate, 0),
                HolidayRate = FieldValue(row.RuleRate, 1),
                WE1Rate = FieldValue(row.RuleRate, 2),
                WE2Rate = FieldValue(row.RuleRate, 3),
                WE3Rate = FieldValue(row.RuleRate, 4),
                PerformsShiftWork = row.RuleShiftWork,
            };
            _context.SchedulingRules.Add(rule);

            if (row.HasRevision)
            {
                _context.SchedulingRuleRateRevisions.Add(new SchedulingRuleRateRevision
                {
                    Id = Guid.NewGuid(),
                    SchedulingRuleId = rule.Id,
                    ValidFrom = RevisionValidFrom,
                    NightRate = FieldValue(row.RevisionRate, 0),
                    HolidayRate = FieldValue(row.RevisionRate, 1),
                    WE1Rate = FieldValue(row.RevisionRate, 2),
                    WE2Rate = FieldValue(row.RevisionRate, 3),
                    WE3Rate = FieldValue(row.RevisionRate, 4),
                });
            }
        }

        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = "Contract",
            PaymentInterval = PaymentInterval.Monthly,
            GuaranteedHours = 160m,
            FullTime = 160m,
            ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            NightRate = FieldValue(row.ContractRate, 0),
            HolidayRate = FieldValue(row.ContractRate, 1),
            WE1Rate = FieldValue(row.ContractRate, 2),
            WE2Rate = FieldValue(row.ContractRate, 3),
            WE3Rate = FieldValue(row.ContractRate, 4),
            PerformsShiftWork = row.ContractShiftWork,
            SchedulingRuleId = rule?.Id,
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
