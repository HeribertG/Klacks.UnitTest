// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for OvertimeSurchargeCalculator (K3): tier-band splitting on day and week basis, the
/// OvertimeThreshold fallback for tier 1, AnalyseToken scenario isolation and the "no configuration"
/// no-op that guarantees unconfigured installations never see an Overtime item.
/// </summary>
using System.Linq;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class OvertimeSurchargeCalculatorTests
{
    private DataBaseContext _context = null!;
    private IClientContractDataProvider _contractDataProvider = null!;
    private IWeekConfiguration _weekConfiguration = null!;
    private OvertimeSurchargeCalculator _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);

        _contractDataProvider = Substitute.For<IClientContractDataProvider>();
        _contractDataProvider.GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>())
            .Returns(new EffectiveContractData());

        _weekConfiguration = Substitute.For<IWeekConfiguration>();

        _sut = new OvertimeSurchargeCalculator(_context, _contractDataProvider, _weekConfiguration);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task CalculateAsync_NoTierSettingsConfigured_ReturnsNotConfigured()
    {
        var work = BuildWork(workTime: 10m);

        var result = await _sut.CalculateAsync(work);

        result.Items.ShouldBeEmpty();
        result.IsConfigured.ShouldBeFalse();
    }

    [Test]
    public async Task CalculateAsync_SingleTierDayBasis_SplitsHoursAboveThreshold()
    {
        await SetSettingAsync(SettingKeys.OvertimeTier1AfterHours, "8");
        await SetSettingAsync(SettingKeys.OvertimeTier1Rate, "0.5");

        var work = BuildWork(workTime: 10m);

        var result = await _sut.CalculateAsync(work);

        result.Items.ShouldHaveSingleItem();
        result.Items.Single().Type.ShouldBe(SurchargeType.Overtime1);
        result.Items.Single().Amount.ShouldBe(1.0m);
    }

    [Test]
    public async Task CalculateAsync_PriorHoursFromOtherWorkSameDay_ShiftsTierBand()
    {
        await SetSettingAsync(SettingKeys.OvertimeTier1AfterHours, "8");
        await SetSettingAsync(SettingKeys.OvertimeTier1Rate, "0.5");

        var clientId = Guid.NewGuid();
        var date = DateOnly.FromDateTime(DateTime.Today);
        await AddOtherWorkAsync(clientId, date, workTime: 6m);

        var work = BuildWork(workTime: 4m, clientId: clientId, date: date);

        var result = await _sut.CalculateAsync(work);

        // periodEnd = 6 + 4 = 10, tier band starts at 8 -> overlap [8,10) = 2h * 0.5 = 1.0
        result.Items.Single().Amount.ShouldBe(1.0m);
    }

    [Test]
    public async Task CalculateAsync_TwoTiers_SplitsAcrossBothBands()
    {
        await SetSettingAsync(SettingKeys.OvertimeTier1AfterHours, "10");
        await SetSettingAsync(SettingKeys.OvertimeTier1Rate, "0.75");
        await SetSettingAsync(SettingKeys.OvertimeTier2AfterHours, "12");
        await SetSettingAsync(SettingKeys.OvertimeTier2Rate, "1.00");

        var work = BuildWork(workTime: 14m);

        var result = await _sut.CalculateAsync(work);

        result.Items.Count.ShouldBe(2);
        var tier1 = result.Items.Single(i => i.Type == SurchargeType.Overtime1);
        var tier2 = result.Items.Single(i => i.Type == SurchargeType.Overtime2);
        tier1.Amount.ShouldBe(1.5m);
        tier2.Amount.ShouldBe(2.0m);
    }

    [Test]
    public async Task CalculateAsync_HoursBelowFirstTier_ReturnsNoItems()
    {
        await SetSettingAsync(SettingKeys.OvertimeTier1AfterHours, "10");
        await SetSettingAsync(SettingKeys.OvertimeTier1Rate, "0.75");

        var work = BuildWork(workTime: 8m);

        var result = await _sut.CalculateAsync(work);

        result.Items.ShouldBeEmpty();
    }

    [Test]
    public async Task CalculateAsync_WeekBasis_CumulatesOtherWorkInSameConfiguredWeek()
    {
        await SetSettingAsync(SettingKeys.OvertimeBasis, "week");
        await SetSettingAsync(SettingKeys.OvertimeTier1AfterHours, "40");
        await SetSettingAsync(SettingKeys.OvertimeTier1Rate, "0.25");

        var clientId = Guid.NewGuid();
        var monday = new DateOnly(2026, 7, 13);
        _weekConfiguration.GetWeekStartAsync(Arg.Any<DateOnly>()).Returns(monday);

        await AddOtherWorkAsync(clientId, monday, workTime: 8m);
        await AddOtherWorkAsync(clientId, monday.AddDays(1), workTime: 8m);
        await AddOtherWorkAsync(clientId, monday.AddDays(2), workTime: 8m);
        await AddOtherWorkAsync(clientId, monday.AddDays(3), workTime: 8m);
        // outside the week (Monday of the following week) must not count
        await AddOtherWorkAsync(clientId, monday.AddDays(7), workTime: 100m);

        var work = BuildWork(workTime: 10m, clientId: clientId, date: monday.AddDays(4));

        var result = await _sut.CalculateAsync(work);

        // priorHours = 32, periodEnd = 42, tier starts at 40 -> overlap [40,42) = 2h * 0.25 = 0.5
        result.Items.Single().Amount.ShouldBe(0.5m);
    }

    [Test]
    public async Task CalculateAsync_OtherWorkInDifferentAnalyseToken_IsExcludedFromPriorHours()
    {
        await SetSettingAsync(SettingKeys.OvertimeTier1AfterHours, "8");
        await SetSettingAsync(SettingKeys.OvertimeTier1Rate, "0.5");

        var clientId = Guid.NewGuid();
        var date = DateOnly.FromDateTime(DateTime.Today);
        var otherScenarioWork = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            CurrentDate = date,
            WorkTime = 20m,
            AnalyseToken = Guid.NewGuid(),
            StartTime = new TimeOnly(6, 0),
            EndTime = new TimeOnly(14, 0),
        };
        _context.Work.Add(otherScenarioWork);
        await _context.SaveChangesAsync();

        var work = BuildWork(workTime: 10m, clientId: clientId, date: date, analyseToken: null);

        var result = await _sut.CalculateAsync(work);

        result.Items.Single().Amount.ShouldBe(1.0m);
    }

    [Test]
    public async Task CalculateAsync_Tier1AfterHoursSettingAbsent_FallsBackToOvertimeThreshold()
    {
        await SetSettingAsync(SettingKeys.OvertimeTier1Rate, "0.5");
        _contractDataProvider.GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>())
            .Returns(new EffectiveContractData { OvertimeThreshold = 8m });

        var work = BuildWork(workTime: 10m);

        var result = await _sut.CalculateAsync(work);

        result.Items.Single().Amount.ShouldBe(1.0m);
    }

    [Test]
    public async Task CalculateAsync_Tier1RateMissing_TierStaysInactiveEvenWithThreshold()
    {
        _contractDataProvider.GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>())
            .Returns(new EffectiveContractData { OvertimeThreshold = 8m });

        var work = BuildWork(workTime: 10m);

        var result = await _sut.CalculateAsync(work);

        result.Items.ShouldBeEmpty();
    }

    private Work BuildWork(decimal workTime, Guid? clientId = null, DateOnly? date = null, Guid? analyseToken = null)
    {
        return new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId ?? Guid.NewGuid(),
            CurrentDate = date ?? DateOnly.FromDateTime(DateTime.Today),
            WorkTime = workTime,
            AnalyseToken = analyseToken,
            StartTime = new TimeOnly(6, 0),
            EndTime = new TimeOnly(6, 0).AddHours((double)workTime),
        };
    }

    private async Task AddOtherWorkAsync(Guid clientId, DateOnly date, decimal workTime)
    {
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            CurrentDate = date,
            WorkTime = workTime,
            StartTime = new TimeOnly(6, 0),
            EndTime = new TimeOnly(6, 0).AddHours((double)workTime),
        });
        await _context.SaveChangesAsync();
    }

    private async Task SetSettingAsync(string type, string value)
    {
        _context.Settings.Add(new Klacks.Api.Domain.Models.Settings.Settings
        {
            Id = Guid.NewGuid(),
            Type = type,
            Value = value,
        });
        await _context.SaveChangesAsync();
    }
}
