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
using Klacks.Api.Domain.Models.Scheduling;
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
    public async Task CalculateAsync_IndustryRuleWithOwnTiers_OverridesGlobalSettings()
    {
        await SetSettingAsync(SettingKeys.OvertimeTier1AfterHours, "8");
        await SetSettingAsync(SettingKeys.OvertimeTier1Rate, "0.5");

        var ruleId = Guid.NewGuid();
        _context.SchedulingRules.Add(new Klacks.Api.Domain.Models.Scheduling.SchedulingRule
        {
            Id = ruleId,
            Name = "Industry preset",
            OvertimeBasis = OvertimeBasis.Day,
            OvertimeTier1AfterHours = 10m,
            OvertimeTier1Rate = 0.75m,
        });
        await _context.SaveChangesAsync();
        _contractDataProvider.GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>())
            .Returns(new EffectiveContractData { SchedulingRuleId = ruleId });

        var work = BuildWork(workTime: 12m);

        var result = await _sut.CalculateAsync(work);

        // Rule tier band starts at 10 (not the global 8): overlap [10,12) = 2h * 0.75 = 1.5
        var item = result.Items.ShouldHaveSingleItem();
        item.Type.ShouldBe(SurchargeType.Overtime1);
        item.Amount.ShouldBe(1.5m);
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

    // Discriminating three-way resolution (Phase 2, dated overtime revisions). One 12h work, three
    // deliberately distinct ladders so a green assertion proves the branch, not just the code path:
    //   settings tier1 = after-8 x 0.5   -> [8,12) = 2.0
    //   base rule tier1 = after-10 x 0.75 -> [10,12) = 1.5   (differs from settings on purpose)
    //   revision tier1 = after-6 x 1.0   -> [6,12) = 6.0

    [Test]
    public async Task CalculateAsync_ApplicableRevisionWithOvertime_OverridesBaseRuleAndSettings()
    {
        await SetSettingAsync(SettingKeys.OvertimeTier1AfterHours, "8");
        await SetSettingAsync(SettingKeys.OvertimeTier1Rate, "0.5");
        var ruleId = await SeedRuleWithBaseOvertimeAsync(baseAfterHours: 10m, baseRate: 0.75m);
        await AddOvertimeRevisionAsync(ruleId, new DateOnly(2027, 1, 1), overtimeAfterHours: 6m, overtimeRate: 1.0m);

        var work = BuildWork(workTime: 12m, date: new DateOnly(2027, 6, 1));

        var result = await _sut.CalculateAsync(work);

        result.Items.Single().Type.ShouldBe(SurchargeType.Overtime1);
        result.Items.Single().Amount.ShouldBe(6.0m);
    }

    [Test]
    public async Task CalculateAsync_WorkDateBeforeRevision_UsesBaseRuleLadder_Retroactive()
    {
        await SetSettingAsync(SettingKeys.OvertimeTier1AfterHours, "8");
        await SetSettingAsync(SettingKeys.OvertimeTier1Rate, "0.5");
        var ruleId = await SeedRuleWithBaseOvertimeAsync(baseAfterHours: 10m, baseRate: 0.75m);
        await AddOvertimeRevisionAsync(ruleId, new DateOnly(2027, 1, 1), overtimeAfterHours: 6m, overtimeRate: 1.0m);

        // February work recomputed later must still see the pre-revision ladder, not the revision.
        var work = BuildWork(workTime: 12m, date: new DateOnly(2026, 2, 15));

        var result = await _sut.CalculateAsync(work);

        result.Items.Single().Amount.ShouldBe(1.5m);
    }

    [Test]
    public async Task CalculateAsync_ApplicableRevisionWithoutOvertime_FallsToSettingsNotBaseRule()
    {
        await SetSettingAsync(SettingKeys.OvertimeTier1AfterHours, "8");
        await SetSettingAsync(SettingKeys.OvertimeTier1Rate, "0.5");
        var ruleId = await SeedRuleWithBaseOvertimeAsync(baseAfterHours: 10m, baseRate: 0.75m);
        // Applicable revision that sets only a rate, no overtime -> full snapshot omits overtime -> settings.
        await AddOvertimeRevisionAsync(ruleId, new DateOnly(2027, 1, 1), overtimeAfterHours: null, overtimeRate: null);

        var work = BuildWork(workTime: 12m, date: new DateOnly(2027, 6, 1));

        var result = await _sut.CalculateAsync(work);

        // 2.0 (settings [8,12)) proves it did NOT fall to the base rule ladder (1.5, [10,12)).
        result.Items.Single().Amount.ShouldBe(2.0m);
    }

    [Test]
    public async Task CalculateAsync_MultipleOvertimeRevisions_UsesLatestOnOrBeforeWorkDate()
    {
        await SetSettingAsync(SettingKeys.OvertimeTier1AfterHours, "8");
        await SetSettingAsync(SettingKeys.OvertimeTier1Rate, "0.5");
        var ruleId = await SeedRuleWithBaseOvertimeAsync(baseAfterHours: 10m, baseRate: 0.75m);
        await AddOvertimeRevisionAsync(ruleId, new DateOnly(2027, 1, 1), overtimeAfterHours: 6m, overtimeRate: 1.0m);
        await AddOvertimeRevisionAsync(ruleId, new DateOnly(2028, 1, 1), overtimeAfterHours: 6m, overtimeRate: 2.0m);

        var early = await _sut.CalculateAsync(BuildWork(workTime: 12m, date: new DateOnly(2027, 6, 1)));
        var late = await _sut.CalculateAsync(BuildWork(workTime: 12m, date: new DateOnly(2028, 6, 1)));

        early.Items.Single().Amount.ShouldBe(6.0m);   // [6,12) * 1.0
        late.Items.Single().Amount.ShouldBe(12.0m);   // [6,12) * 2.0
    }

    [Test]
    public async Task CalculateAsync_NoRevisions_BaseRuleLadderUnchanged_RegressionGuard()
    {
        await SetSettingAsync(SettingKeys.OvertimeTier1AfterHours, "8");
        await SetSettingAsync(SettingKeys.OvertimeTier1Rate, "0.5");
        await SeedRuleWithBaseOvertimeAsync(baseAfterHours: 10m, baseRate: 0.75m);

        var work = BuildWork(workTime: 12m, date: new DateOnly(2027, 6, 1));

        var result = await _sut.CalculateAsync(work);

        result.Items.Single().Amount.ShouldBe(1.5m);
    }

    private async Task<Guid> SeedRuleWithBaseOvertimeAsync(decimal baseAfterHours, decimal baseRate)
    {
        var ruleId = Guid.NewGuid();
        _context.SchedulingRules.Add(new SchedulingRule
        {
            Id = ruleId,
            Name = "Industry preset",
            OvertimeBasis = OvertimeBasis.Day,
            OvertimeTier1AfterHours = baseAfterHours,
            OvertimeTier1Rate = baseRate,
        });
        await _context.SaveChangesAsync();
        _contractDataProvider.GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>())
            .Returns(new EffectiveContractData { SchedulingRuleId = ruleId });
        return ruleId;
    }

    private async Task AddOvertimeRevisionAsync(Guid ruleId, DateOnly validFrom, decimal? overtimeAfterHours, decimal? overtimeRate)
    {
        _context.SchedulingRuleRateRevisions.Add(new SchedulingRuleRateRevision
        {
            Id = Guid.NewGuid(),
            SchedulingRuleId = ruleId,
            ValidFrom = validFrom,
            NightRate = 0.5m,
            OvertimeBasis = overtimeAfterHours.HasValue ? OvertimeBasis.Day : null,
            OvertimeTier1AfterHours = overtimeAfterHours,
            OvertimeTier1Rate = overtimeRate,
        });
        await _context.SaveChangesAsync();
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

    // Starts at 05:00 — strictly before BuildWork's 06:00 — so a same-day "other" work sorts as a prior
    // deterministically. With identical (CurrentDate, StartTime) the calculator's partition order falls
    // back to the Id comparison, which is random-GUID coin-flipping in a test.
    private async Task AddOtherWorkAsync(Guid clientId, DateOnly date, decimal workTime)
    {
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            CurrentDate = date,
            WorkTime = workTime,
            StartTime = new TimeOnly(5, 0),
            EndTime = new TimeOnly(5, 0).AddHours((double)workTime),
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
