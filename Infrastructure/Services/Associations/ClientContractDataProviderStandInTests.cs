// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests the stand-in branch of ClientContractDataProvider: a stand-in without a contract has no target hours
/// (guaranteed and minimum hours 0, also against a company-wide monthly row) and keeps every other default (rest,
/// maximum hours, consecutive days), on the single-date, batch and range paths; a contractless employee keeps the
/// settings values.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.Associations;

using System.Globalization;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
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
public class ClientContractDataProviderStandInTests
{
    private const decimal SettingsGuaranteedHours = 170m;
    private const decimal SettingsMinimumHours = 160m;
    private const decimal SettingsMaximumHours = 190m;
    private const decimal SettingsMaxWeeklyHours = 50m;
    private const decimal SettingsMinPauseHours = 11m;
    private const decimal JanuaryTargetHours = 184m;

    private static readonly DateOnly January = new(2026, 1, 15);

    private DataBaseContext _context = null!;
    private ClientContractDataProvider _sut = null!;
    private Guid _standInId;
    private Guid _employeeId;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _sut = new ClientContractDataProvider(_context, NullLogger<ClientContractDataProvider>.Instance, new SettingsChangeVersion());

        AddSetting(SettingKeys.GuaranteedHours, SettingsGuaranteedHours);
        AddSetting(SettingKeys.MinimumHours, SettingsMinimumHours);
        AddSetting(SettingKeys.MaximumHours, SettingsMaximumHours);
        AddSetting(SettingKeys.SchedulingMaxWeeklyHours, SettingsMaxWeeklyHours);
        AddSetting(SettingKeys.SchedulingMinPauseHours, SettingsMinPauseHours);

        _standInId = AddClient(EntityTypeEnum.StandIn);
        _employeeId = AddClient(EntityTypeEnum.Employee);
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task StandIn_WithoutContract_HasZeroGuaranteedAndMinimum_KeepsRestDefaults()
    {
        var standIn = await _sut.GetEffectiveContractDataAsync(_standInId, January);
        var employee = await _sut.GetEffectiveContractDataAsync(_employeeId, January);

        standIn.GuaranteedHours.ShouldBe(0m);
        standIn.MinimumHours.ShouldBe(0m);
        standIn.MaximumHours.ShouldBe(SettingsMaximumHours);
        standIn.MaxWeeklyHours.ShouldBe(SettingsMaxWeeklyHours);
        standIn.MinPauseHours.ShouldBe(SettingsMinPauseHours);
        standIn.MaxConsecutiveDays.ShouldBe(employee.MaxConsecutiveDays);
        standIn.MaxWorkDays.ShouldBe(employee.MaxWorkDays);
        standIn.MinRestDays.ShouldBe(employee.MinRestDays);
        standIn.HasActiveContract.ShouldBeFalse();

        employee.GuaranteedHours.ShouldBe(SettingsGuaranteedHours);
        employee.MinimumHours.ShouldBe(SettingsMinimumHours);
    }

    [Test]
    public async Task StandIn_IgnoresTheCompanyMonthlyTargetHours_TheEmployeeTakesThem()
    {
        _context.MonthlyTargetHours.Add(new MonthlyTargetHours
        {
            Id = Guid.NewGuid(),
            Year = January.Year,
            Month = January.Month,
            Hours = JanuaryTargetHours,
        });
        await _context.SaveChangesAsync();

        var result = await _sut.GetEffectiveContractDataForClientsAsync([_standInId, _employeeId], January);

        result[_standInId].GuaranteedHours.ShouldBe(0m);
        result[_standInId].MinimumHours.ShouldBe(0m);
        result[_employeeId].GuaranteedHours.ShouldBe(JanuaryTargetHours);
    }

    [Test]
    public async Task Range_StandIn_HasZeroOnEveryDay_TheEmployeeKeepsTheSettings()
    {
        var until = January.AddDays(2);

        var result = await _sut.GetEffectiveContractDataForClientsRangeAsync([_standInId, _employeeId], January, until);

        result.Count.ShouldBe(3);
        foreach (var perDay in result.Values)
        {
            perDay[_standInId].GuaranteedHours.ShouldBe(0m);
            perDay[_standInId].MinimumHours.ShouldBe(0m);
            perDay[_standInId].MaxWeeklyHours.ShouldBe(SettingsMaxWeeklyHours);
            perDay[_employeeId].GuaranteedHours.ShouldBe(SettingsGuaranteedHours);
            perDay[_employeeId].MinimumHours.ShouldBe(SettingsMinimumHours);
        }
    }

    private Guid AddClient(EntityTypeEnum type)
    {
        var client = new Client { Id = Guid.NewGuid(), Name = "Client", Type = type };
        _context.Client.Add(client);
        return client.Id;
    }

    private void AddSetting(string key, decimal value)
    {
        _context.Settings.Add(new SettingsEntity
        {
            Id = Guid.NewGuid(),
            Type = key,
            Value = value.ToString(CultureInfo.InvariantCulture),
        });
    }
}
