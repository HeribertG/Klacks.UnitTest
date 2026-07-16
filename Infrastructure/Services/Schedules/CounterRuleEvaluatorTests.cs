// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for CounterRuleEvaluator (K18): night-shift counting against the resolved night window
/// (including cross-midnight), distinct worked days per ISO week, overlong-shift counting, planned-slot
/// projection, industry scoping and block-mode escalation.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class CounterRuleEvaluatorTests
{
    private static readonly DateOnly Monday = new(2026, 7, 13);

    private DataBaseContext _context = null!;
    private ICounterRuleRepository _ruleRepository = null!;
    private IComplianceEnforcementResolver _enforcementResolver = null!;
    private IClientContractDataProvider _contractDataProvider = null!;
    private CounterRuleEvaluator _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);

        _ruleRepository = Substitute.For<ICounterRuleRepository>();
        _ruleRepository.GetAllActiveAsync().Returns(new List<CounterRule>());

        _enforcementResolver = Substitute.For<IComplianceEnforcementResolver>();
        _enforcementResolver.GetModeAsync(ComplianceRuleNames.CounterRule).Returns(RuleEnforcementMode.Warn);

        _contractDataProvider = Substitute.For<IClientContractDataProvider>();
        _contractDataProvider
            .GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData());

        _sut = new CounterRuleEvaluator(_ruleRepository, _context, _enforcementResolver, _contractDataProvider);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task EvaluateAsync_NoActiveRules_ReturnsEmpty()
    {
        (await _sut.EvaluateAsync(Guid.NewGuid(), "Anna", Monday)).ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_NightShiftsReachYearThreshold_ReportsViolation()
    {
        StubRule(CounterEventType.NightShift, CounterPeriod.Year, threshold: 3);
        var clientId = Guid.NewGuid();
        SeedWork(clientId, new DateOnly(2026, 2, 3), new TimeOnly(22, 0), new TimeOnly(6, 0));
        SeedWork(clientId, new DateOnly(2026, 4, 10), new TimeOnly(23, 30), new TimeOnly(7, 0));
        SeedWork(clientId, new DateOnly(2026, 7, 11), new TimeOnly(22, 0), new TimeOnly(7, 0));
        SeedWork(clientId, new DateOnly(2026, 5, 5), new TimeOnly(8, 0), new TimeOnly(16, 0));

        var result = await _sut.EvaluateAsync(clientId, "Anna", Monday);

        var entry = result.ShouldHaveSingleItem();
        entry.Comment.ShouldBe(ScheduleValidationKeys.CounterRule);
        entry.CommentParams["event"].ShouldBe("NightShift");
        entry.CommentParams["count"].ShouldBe("3");
        entry.CommentParams["threshold"].ShouldBe("3");
        entry.CommentParams["period"].ShouldBe("Year");
    }

    [Test]
    public async Task EvaluateAsync_NightShiftsBelowThreshold_ReturnsEmpty()
    {
        StubRule(CounterEventType.NightShift, CounterPeriod.Year, threshold: 3);
        var clientId = Guid.NewGuid();
        SeedWork(clientId, new DateOnly(2026, 2, 3), new TimeOnly(22, 0), new TimeOnly(6, 0));
        SeedWork(clientId, new DateOnly(2026, 4, 10), new TimeOnly(23, 30), new TimeOnly(7, 0));

        (await _sut.EvaluateAsync(clientId, "Anna", Monday)).ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_DayShiftsDoNotCountAsNightShifts()
    {
        StubRule(CounterEventType.NightShift, CounterPeriod.Year, threshold: 1);
        var clientId = Guid.NewGuid();
        SeedWork(clientId, new DateOnly(2026, 2, 3), new TimeOnly(8, 0), new TimeOnly(16, 0));
        SeedWork(clientId, new DateOnly(2026, 2, 4), new TimeOnly(6, 30), new TimeOnly(14, 0));

        (await _sut.EvaluateAsync(clientId, "Anna", Monday)).ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_ContractNightWindowOverride_CountsAgainstTheEffectiveWindow()
    {
        // A 20:30-22:00 shift is outside the default 23:00-06:00 window but inside the client's
        // contract-level 20:00-05:00 override - the evaluator must use the effective K2 chain.
        StubRule(CounterEventType.NightShift, CounterPeriod.Year, threshold: 1);
        var clientId = Guid.NewGuid();
        _contractDataProvider
            .GetEffectiveContractDataAsync(clientId, Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData { NightStart = "20:00", NightEnd = "05:00" });
        SeedWork(clientId, new DateOnly(2026, 3, 3), new TimeOnly(20, 30), new TimeOnly(22, 0));

        (await _sut.EvaluateAsync(clientId, "Anna", Monday)).ShouldHaveSingleItem();
    }

    [Test]
    public async Task EvaluateAsync_CrossMidnightShiftDuration_CountsAgainstHoursThreshold()
    {
        StubRule(CounterEventType.ShiftExceedingHours, CounterPeriod.Month, threshold: 1, hoursThreshold: 13m);
        var clientId = Guid.NewGuid();
        SeedWork(clientId, new DateOnly(2026, 7, 2), new TimeOnly(18, 0), new TimeOnly(8, 0));

        var entry = (await _sut.EvaluateAsync(clientId, "Anna", Monday)).ShouldHaveSingleItem();
        entry.CommentParams["count"].ShouldBe("1");
    }

    [Test]
    public async Task EvaluateAsync_SixDistinctWorkedDaysInWeek_ReportsViolation()
    {
        StubRule(CounterEventType.WorkedDayInWeek, CounterPeriod.Week, threshold: 6);
        var clientId = Guid.NewGuid();
        for (var i = 0; i < 6; i++)
        {
            SeedWork(clientId, Monday.AddDays(i), new TimeOnly(8, 0), new TimeOnly(16, 0));
        }

        SeedWork(clientId, Monday, new TimeOnly(18, 0), new TimeOnly(20, 0));

        var result = await _sut.EvaluateAsync(clientId, "Anna", Monday.AddDays(5));

        result.ShouldHaveSingleItem().CommentParams["count"].ShouldBe("6");
    }

    [Test]
    public async Task EvaluateAsync_FiveWorkedDaysInWeek_ReturnsEmpty()
    {
        StubRule(CounterEventType.WorkedDayInWeek, CounterPeriod.Week, threshold: 6);
        var clientId = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
        {
            SeedWork(clientId, Monday.AddDays(i), new TimeOnly(8, 0), new TimeOnly(16, 0));
        }

        (await _sut.EvaluateAsync(clientId, "Anna", Monday.AddDays(4))).ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_OverlongShiftsReachMonthThreshold_ReportsViolation()
    {
        StubRule(CounterEventType.ShiftExceedingHours, CounterPeriod.Month, threshold: 2, hoursThreshold: 13m);
        var clientId = Guid.NewGuid();
        SeedWork(clientId, new DateOnly(2026, 7, 2), new TimeOnly(7, 0), new TimeOnly(21, 30));
        SeedWork(clientId, new DateOnly(2026, 7, 9), new TimeOnly(6, 0), new TimeOnly(20, 0));
        SeedWork(clientId, new DateOnly(2026, 7, 15), new TimeOnly(8, 0), new TimeOnly(20, 0));

        var result = await _sut.EvaluateAsync(clientId, "Anna", Monday);

        result.ShouldHaveSingleItem().CommentParams["count"].ShouldBe("2");
    }

    [Test]
    public async Task EvaluatePlannedAsync_PlannedNightSlotCompletesTheCount_ReportsViolation()
    {
        StubRule(CounterEventType.NightShift, CounterPeriod.Year, threshold: 2);
        var clientId = Guid.NewGuid();
        SeedWork(clientId, new DateOnly(2026, 2, 3), new TimeOnly(22, 0), new TimeOnly(6, 0));

        var result = await _sut.EvaluatePlannedAsync(
            clientId,
            "Anna",
            [(new DateOnly(2026, 7, 11), new TimeOnly(22, 0), new TimeOnly(7, 0))]);

        result.ShouldHaveSingleItem().CommentParams["count"].ShouldBe("2");
    }

    [Test]
    public async Task EvaluateAsync_BlockMode_EscalatesToErrorWithEnforcementTag()
    {
        StubRule(CounterEventType.NightShift, CounterPeriod.Year, threshold: 1);
        _enforcementResolver.GetModeAsync(ComplianceRuleNames.CounterRule).Returns(RuleEnforcementMode.Block);
        var clientId = Guid.NewGuid();
        SeedWork(clientId, new DateOnly(2026, 3, 3), new TimeOnly(23, 0), new TimeOnly(6, 0));

        var entry = (await _sut.EvaluateAsync(clientId, "Anna", Monday)).ShouldHaveSingleItem();

        entry.Type.ShouldBe(ScheduleValidationType.Error);
        entry.CommentParams[ComplianceRuleNames.EnforcementRuleParamKey].ShouldBe(ComplianceRuleNames.CounterRule);
    }

    [Test]
    public async Task EvaluateAsync_RuleBlockOverride_GlobalWarn_EscalatesToError()
    {
        // Global mode is Warn (SetUp); the rule's own Block override must win for this rule only.
        StubRule(CounterEventType.NightShift, CounterPeriod.Year, threshold: 1, enforcement: RuleEnforcementMode.Block);
        var clientId = Guid.NewGuid();
        SeedWork(clientId, new DateOnly(2026, 3, 3), new TimeOnly(23, 0), new TimeOnly(6, 0));

        var entry = (await _sut.EvaluateAsync(clientId, "Anna", Monday)).ShouldHaveSingleItem();

        entry.Type.ShouldBe(ScheduleValidationType.Error);
        entry.CommentParams[ComplianceRuleNames.EnforcementRuleParamKey].ShouldBe(ComplianceRuleNames.CounterRule);
    }

    [Test]
    public async Task EvaluateAsync_RuleWarnOverride_GlobalBlock_StaysWarning()
    {
        StubRule(CounterEventType.NightShift, CounterPeriod.Year, threshold: 1, enforcement: RuleEnforcementMode.Warn);
        _enforcementResolver.GetModeAsync(ComplianceRuleNames.CounterRule).Returns(RuleEnforcementMode.Block);
        var clientId = Guid.NewGuid();
        SeedWork(clientId, new DateOnly(2026, 3, 3), new TimeOnly(23, 0), new TimeOnly(6, 0));

        var entry = (await _sut.EvaluateAsync(clientId, "Anna", Monday)).ShouldHaveSingleItem();

        entry.Type.ShouldBe(ScheduleValidationType.Warning);
        entry.CommentParams.ContainsKey(ComplianceRuleNames.EnforcementRuleParamKey).ShouldBeFalse();
    }

    [Test]
    public async Task EvaluateAsync_RuleNullOverride_FollowsGlobalBlockMode()
    {
        StubRule(CounterEventType.NightShift, CounterPeriod.Year, threshold: 1, enforcement: null);
        _enforcementResolver.GetModeAsync(ComplianceRuleNames.CounterRule).Returns(RuleEnforcementMode.Block);
        var clientId = Guid.NewGuid();
        SeedWork(clientId, new DateOnly(2026, 3, 3), new TimeOnly(23, 0), new TimeOnly(6, 0));

        var entry = (await _sut.EvaluateAsync(clientId, "Anna", Monday)).ShouldHaveSingleItem();

        entry.Type.ShouldBe(ScheduleValidationType.Error);
    }

    [Test]
    public async Task EvaluateAsync_IndustryScopedRule_AppliesOnlyToMatchingContractRule()
    {
        var boundRuleId = Guid.NewGuid();
        StubRule(CounterEventType.NightShift, CounterPeriod.Year, threshold: 1, schedulingRuleId: boundRuleId);
        var clientId = Guid.NewGuid();
        SeedWork(clientId, new DateOnly(2026, 3, 3), new TimeOnly(23, 0), new TimeOnly(6, 0));

        _contractDataProvider
            .GetEffectiveContractDataAsync(clientId, Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData { SchedulingRuleId = Guid.NewGuid() });
        (await _sut.EvaluateAsync(clientId, "Anna", Monday)).ShouldBeEmpty();

        _contractDataProvider
            .GetEffectiveContractDataAsync(clientId, Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData { SchedulingRuleId = boundRuleId });
        (await _sut.EvaluateAsync(clientId, "Anna", Monday)).ShouldHaveSingleItem();
    }

    private void StubRule(
        CounterEventType eventType,
        CounterPeriod period,
        int threshold,
        decimal? hoursThreshold = null,
        Guid? schedulingRuleId = null,
        RuleEnforcementMode? enforcement = null)
    {
        _ruleRepository.GetAllActiveAsync().Returns(new List<CounterRule>
        {
            new()
            {
                Id = Guid.NewGuid(),
                EventType = eventType,
                Period = period,
                Threshold = threshold,
                HoursThreshold = hoursThreshold,
                SchedulingRuleId = schedulingRuleId,
                Enforcement = enforcement,
            },
        });
    }

    private void SeedWork(Guid clientId, DateOnly date, TimeOnly start, TimeOnly end)
    {
        _context.Work.Add(new Klacks.Api.Domain.Models.Schedules.Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = Guid.NewGuid(),
            CurrentDate = date,
            StartTime = start,
            EndTime = end,
            WorkTime = 8m,
        });
        _context.SaveChanges();
    }
}
