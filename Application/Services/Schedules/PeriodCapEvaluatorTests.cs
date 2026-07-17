// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for PeriodCapEvaluator. Fixed-period mode (K5 stage 1, TotalHours scope): no notification
/// while the persisted-plus-planned total stays at or under the cap, a Warning once it exceeds the cap
/// under Warn enforcement, and an Error for the same breach once the PeriodCap compliance rule is set to
/// Block. Overtime-cap mode (OvertimeHours scope): overtime summed per day or week against the resolved
/// overtime definition (tier ladder or contract-threshold fallback), week attribution by week start,
/// skip when overtime is undefined, and the CustomWeeks trailing window. Rolling-average mode (K6): same
/// warn/block escalation for the average-over-window comparison, plus the employment-start clamp that
/// prevents padding a short history with non-existent zero weeks.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Scheduling;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class PeriodCapEvaluatorTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 3, 15);

    private IPeriodCapRuleRepository _ruleRepository = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private IComplianceEnforcementResolver _enforcementResolver = null!;
    private IClientMembershipStartResolver _membershipStartResolver = null!;
    private IClientContractDataProvider _contractDataProvider = null!;
    private IOvertimeConfigResolver _overtimeConfigResolver = null!;
    private IClientWorkHoursProvider _workHoursProvider = null!;
    private IWeekConfiguration _weekConfiguration = null!;
    private IOnCallConfigResolver _onCallConfigResolver = null!;
    private IClientOnCallHoursProvider _onCallHoursProvider = null!;
    private PeriodCapEvaluator _evaluator = null!;

    [SetUp]
    public void SetUp()
    {
        _ruleRepository = Substitute.For<IPeriodCapRuleRepository>();
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _enforcementResolver = Substitute.For<IComplianceEnforcementResolver>();
        _enforcementResolver.GetModeAsync(ComplianceRuleNames.PeriodCap).Returns(RuleEnforcementMode.Warn);
        _enforcementResolver.GetModeAsync(ComplianceRuleNames.RollingAverage).Returns(RuleEnforcementMode.Warn);
        _membershipStartResolver = Substitute.For<IClientMembershipStartResolver>();
        _membershipStartResolver.GetValidFromAsync(Arg.Any<Guid>()).Returns((DateOnly?)null);

        _contractDataProvider = Substitute.For<IClientContractDataProvider>();
        _contractDataProvider
            .GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData());

        _overtimeConfigResolver = Substitute.For<IOvertimeConfigResolver>();
        _overtimeConfigResolver.ResolveAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>())
            .Returns(new OvertimeSurchargeConfig());

        _workHoursProvider = Substitute.For<IClientWorkHoursProvider>();
        _workHoursProvider
            .GetWorkHoursByDayAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>())
            .Returns(new Dictionary<DateOnly, decimal>());

        // Monday-based weeks, matching the production default of WeekConfiguration.
        _weekConfiguration = Substitute.For<IWeekConfiguration>();
        _weekConfiguration.GetWeekStartAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var date = callInfo.ArgAt<DateOnly>(0);
                var offset = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
                return date.AddDays(-offset);
            });

        // On-call disabled by default: no subtraction from the cap baseline, so all existing cases are
        // unaffected. Individual on-call tests re-stub these two.
        _onCallConfigResolver = Substitute.For<IOnCallConfigResolver>();
        _onCallConfigResolver.ResolveAsync().Returns(new OnCallConfig(false, 1m, 0m, false));
        _onCallHoursProvider = Substitute.For<IClientOnCallHoursProvider>();
        _onCallHoursProvider
            .GetWeightedOnCallHoursAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<OnCallConfig>())
            .Returns(0m);

        _evaluator = new PeriodCapEvaluator(
            _ruleRepository,
            _periodHoursService,
            _enforcementResolver,
            _membershipStartResolver,
            _contractDataProvider,
            _overtimeConfigResolver,
            _workHoursProvider,
            _weekConfiguration,
            _onCallConfigResolver,
            _onCallHoursProvider);
    }

    private void StubRule(decimal capHours)
    {
        _ruleRepository.GetAllActiveAsync().Returns(new List<PeriodCapRule>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Period = PeriodCapPeriod.Month,
                Scope = PeriodCapScope.TotalHours,
                CapHours = capHours,
                ImportSourceKey = "region-setup:compliance.periodCaps:month:totalhours",
                ImportContentHash = "irrelevant-for-this-test",
            },
        });
    }

    private void StubRollingRule(int windowWeeks, decimal maxAverageWeeklyHours, Guid? schedulingRuleId = null)
    {
        _ruleRepository.GetAllActiveAsync().Returns(new List<PeriodCapRule>
        {
            new()
            {
                Id = Guid.NewGuid(),
                RollingWindowWeeks = windowWeeks,
                MaxAverageWeeklyHours = maxAverageWeeklyHours,
                SchedulingRuleId = schedulingRuleId,
                ImportSourceKey = $"region-setup:compliance.periodCaps:rolling:{windowWeeks}w",
                ImportContentHash = "irrelevant-for-this-test",
            },
        });
    }

    [Test]
    public async Task EvaluateAsync_IndustryScopedRollingRule_AppliesOnlyToMatchingContractRule()
    {
        var boundRuleId = Guid.NewGuid();
        StubRollingRule(windowWeeks: 17, maxAverageWeeklyHours: 48m, schedulingRuleId: boundRuleId);
        StubPersistedHours(hours: 17 * 60m);

        _contractDataProvider
            .GetEffectiveContractDataAsync(ClientId, Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData { SchedulingRuleId = Guid.NewGuid() });
        (await _evaluator.EvaluateAsync(ClientId, "Jane Doe", Day)).ShouldBeEmpty();

        _contractDataProvider
            .GetEffectiveContractDataAsync(ClientId, Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData { SchedulingRuleId = boundRuleId });
        (await _evaluator.EvaluateAsync(ClientId, "Jane Doe", Day)).ShouldHaveSingleItem();
    }

    private void StubPersistedHours(decimal hours)
    {
        _periodHoursService
            .CalculatePeriodHoursAsync(ClientId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>())
            .Returns(new PeriodHoursResource { Hours = hours, Surcharges = 0m, GuaranteedHours = 0m });
    }

    private void StubOnCall(bool enabled, bool includeInPeriodCaps, decimal weightedHours)
    {
        _onCallConfigResolver.ResolveAsync().Returns(new OnCallConfig(enabled, 1.0m, 0.25m, includeInPeriodCaps));
        _onCallHoursProvider
            .GetWeightedOnCallHoursAsync(ClientId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<OnCallConfig>())
            .Returns(weightedHours);
    }

    [Test]
    public async Task EvaluateAsync_OnCallExcludedFromCaps_SubtractsWeightedFromBaseline_NoBreach()
    {
        StubRule(capHours: 200m);
        StubPersistedHours(hours: 210m); // baseline already includes 20 weighted on-call hours
        StubOnCall(enabled: true, includeInPeriodCaps: false, weightedHours: 20m);

        var result = await _evaluator.EvaluateAsync(ClientId, "Jane Doe", Day);

        // 210 - 20 = 190 <= cap 200 -> on-call must not push the cap over.
        result.ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_OnCallIncludedInCaps_NotSubtracted_ReturnsWarning()
    {
        StubRule(capHours: 200m);
        StubPersistedHours(hours: 210m);
        StubOnCall(enabled: true, includeInPeriodCaps: true, weightedHours: 20m);

        var result = await _evaluator.EvaluateAsync(ClientId, "Jane Doe", Day);

        // includeInPeriodCaps -> no subtraction -> 210 > 200.
        result.Count.ShouldBe(1);
        result[0].Type.ShouldBe(ScheduleValidationType.Warning);
        _onCallHoursProvider.DidNotReceiveWithAnyArgs()
            .GetWeightedOnCallHoursAsync(default, default, default, default, default!);
    }

    [Test]
    public async Task EvaluateAsync_RollingAverage_OnCallExcluded_SubtractsFromBaseline()
    {
        StubRollingRule(windowWeeks: 2, maxAverageWeeklyHours: 48m);
        StubPersistedHours(hours: 100m); // 100/2 = 50 > 48 without the on-call subtraction
        StubOnCall(enabled: true, includeInPeriodCaps: false, weightedHours: 20m);

        var result = await _evaluator.EvaluateAsync(ClientId, "Jane Doe", Day);

        // (100 - 20) / 2 = 40 <= 48 -> no breach.
        result.ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_OvertimeCapScope_OnCallNeverLeaksIntoOvertime_Regression()
    {
        StubOvertimeRule(capHours: 10m);
        StubOvertimeConfig(OvertimeBasis.Week, tier1AfterHours: 40m);
        StubWorkHours(
            (MondayInMarch, 10m),
            (MondayInMarch.AddDays(1), 10m),
            (MondayInMarch.AddDays(2), 10m),
            (MondayInMarch.AddDays(3), 10m),
            (MondayInMarch.AddDays(4), 8m)); // 48h week -> 8h overtime, under cap 10
        // A large on-call amount that WOULD blow past the cap if it leaked into the overtime bucket.
        StubOnCall(enabled: true, includeInPeriodCaps: false, weightedHours: 100m);

        var result = await _evaluator.EvaluateAsync(ClientId, "Jane Doe", Day);

        // Overtime is fed exclusively from Work rows (IClientWorkHoursProvider); on-call can neither add
        // to nor be subtracted from the overtime bucket. The on-call provider is never consulted here.
        result.ShouldBeEmpty();
        _onCallHoursProvider.DidNotReceiveWithAnyArgs()
            .GetWeightedOnCallHoursAsync(default, default, default, default, default!);
    }

    [Test]
    public async Task EvaluateAsync_UnderCap_ReturnsNoNotification()
    {
        StubRule(capHours: 200m);
        StubPersistedHours(hours: 150m);

        var result = await _evaluator.EvaluateAsync(ClientId, "Jane Doe", Day);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_AtExactCap_ReturnsNoNotification()
    {
        StubRule(capHours: 200m);
        StubPersistedHours(hours: 200m);

        var result = await _evaluator.EvaluateAsync(ClientId, "Jane Doe", Day);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_OverCap_ReturnsWarning()
    {
        StubRule(capHours: 200m);
        StubPersistedHours(hours: 210m);

        var result = await _evaluator.EvaluateAsync(ClientId, "Jane Doe", Day);

        result.Count.ShouldBe(1);
        result[0].Type.ShouldBe(ScheduleValidationType.Warning);
        result[0].Comment.ShouldBe(ScheduleValidationKeys.PeriodCap);
        result[0].ClientId.ShouldBe(ClientId);
        result[0].CommentParams.ShouldContainKeyAndValue("actualHours", "210.0");
        result[0].CommentParams.ShouldContainKeyAndValue("capHours", "200");
        result[0].CommentParams.ShouldNotContainKey(ComplianceRuleNames.EnforcementRuleParamKey);
    }

    [Test]
    public async Task EvaluateAsync_OverCapWithBlockEnforcement_ReturnsError()
    {
        StubRule(capHours: 200m);
        StubPersistedHours(hours: 210m);
        _enforcementResolver.GetModeAsync(ComplianceRuleNames.PeriodCap).Returns(RuleEnforcementMode.Block);

        var result = await _evaluator.EvaluateAsync(ClientId, "Jane Doe", Day);

        result.Count.ShouldBe(1);
        result[0].Type.ShouldBe(ScheduleValidationType.Error);
        result[0].CommentParams.ShouldContainKeyAndValue(ComplianceRuleNames.EnforcementRuleParamKey, ComplianceRuleNames.PeriodCap);
    }

    [Test]
    public async Task EvaluateAsync_NoActiveRules_ReturnsNoNotification()
    {
        _ruleRepository.GetAllActiveAsync().Returns(new List<PeriodCapRule>());

        var result = await _evaluator.EvaluateAsync(ClientId, "Jane Doe", Day);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluatePlannedAsync_PersistedUnderCapPlusPlannedDelta_ReturnsWarningOnce()
    {
        StubRule(capHours: 200m);
        StubPersistedHours(hours: 190m);

        var result = await _evaluator.EvaluatePlannedAsync(
            ClientId,
            "Jane Doe",
            new List<(DateOnly Date, decimal Hours)> { (Day, 8m), (Day.AddDays(1), 8m) });

        result.Count.ShouldBe(1);
        result[0].Type.ShouldBe(ScheduleValidationType.Warning);
        result[0].CommentParams["actualHours"].ShouldBe("206.0");
    }

    [Test]
    public async Task EvaluatePlannedAsync_PersistedPlusPlannedStaysUnderCap_ReturnsNoNotification()
    {
        StubRule(capHours: 200m);
        StubPersistedHours(hours: 150m);

        var result = await _evaluator.EvaluatePlannedAsync(
            ClientId,
            "Jane Doe",
            new List<(DateOnly Date, decimal Hours)> { (Day, 8m) });

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_RollingAverageUnderThreshold_ReturnsNoNotification()
    {
        StubRollingRule(windowWeeks: 17, maxAverageWeeklyHours: 48m);
        StubPersistedHours(hours: 680m);

        var result = await _evaluator.EvaluateAsync(ClientId, "Jane Doe", Day);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_RollingAverageOverThreshold_ReturnsWarning()
    {
        StubRollingRule(windowWeeks: 17, maxAverageWeeklyHours: 48m);
        StubPersistedHours(hours: 850m);

        var result = await _evaluator.EvaluateAsync(ClientId, "Jane Doe", Day);

        result.Count.ShouldBe(1);
        result[0].Type.ShouldBe(ScheduleValidationType.Warning);
        result[0].Comment.ShouldBe(ScheduleValidationKeys.RollingAverage);
        result[0].ClientId.ShouldBe(ClientId);
        result[0].CommentParams.ShouldContainKeyAndValue("actualHours", "50.0");
        result[0].CommentParams.ShouldContainKeyAndValue("capHours", "48");
        result[0].CommentParams.ShouldContainKeyAndValue("windowWeeks", "17");
        result[0].CommentParams.ShouldNotContainKey(ComplianceRuleNames.EnforcementRuleParamKey);
    }

    [Test]
    public async Task EvaluateAsync_RollingAverageOverThresholdWithBlockEnforcement_ReturnsError()
    {
        StubRollingRule(windowWeeks: 17, maxAverageWeeklyHours: 48m);
        StubPersistedHours(hours: 850m);
        _enforcementResolver.GetModeAsync(ComplianceRuleNames.RollingAverage).Returns(RuleEnforcementMode.Block);

        var result = await _evaluator.EvaluateAsync(ClientId, "Jane Doe", Day);

        result.Count.ShouldBe(1);
        result[0].Type.ShouldBe(ScheduleValidationType.Error);
        result[0].CommentParams.ShouldContainKeyAndValue(ComplianceRuleNames.EnforcementRuleParamKey, ComplianceRuleNames.RollingAverage);
    }

    [Test]
    public async Task EvaluateAsync_RollingAverageLessHistoryThanWindow_AveragesOverActualWeeksOnly()
    {
        StubRollingRule(windowWeeks: 17, maxAverageWeeklyHours: 48m);
        var membershipStart = Day.AddDays(-6);
        _membershipStartResolver.GetValidFromAsync(ClientId).Returns((DateOnly?)membershipStart);

        _periodHoursService
            .CalculatePeriodHoursAsync(ClientId, membershipStart, Day, Arg.Any<Guid?>())
            .Returns(new PeriodHoursResource { Hours = 50m, Surcharges = 0m, GuaranteedHours = 0m });

        var result = await _evaluator.EvaluateAsync(ClientId, "Jane Doe", Day);

        result.Count.ShouldBe(1);
        result[0].CommentParams.ShouldContainKeyAndValue("actualHours", "50.0");
    }

    [Test]
    public async Task EvaluateAsync_RollingAverageLessThanOneWeekHistory_ReturnsNoNotificationAndSkipsQuery()
    {
        StubRollingRule(windowWeeks: 17, maxAverageWeeklyHours: 48m);
        var membershipStart = Day.AddDays(-3);
        _membershipStartResolver.GetValidFromAsync(ClientId).Returns((DateOnly?)membershipStart);
        StubPersistedHours(hours: 1000m);

        var result = await _evaluator.EvaluateAsync(ClientId, "Jane Doe", Day);

        result.ShouldBeEmpty();
        await _periodHoursService.DidNotReceiveWithAnyArgs()
            .CalculatePeriodHoursAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>());
    }

    private void StubOvertimeRule(decimal capHours, PeriodCapPeriod period = PeriodCapPeriod.Month, int? customPeriodWeeks = null)
    {
        _ruleRepository.GetAllActiveAsync().Returns(new List<PeriodCapRule>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Period = period,
                Scope = PeriodCapScope.OvertimeHours,
                CapHours = capHours,
                CustomPeriodWeeks = customPeriodWeeks,
                ImportSourceKey = "region-setup:compliance.periodCaps:month:overtimehours",
                ImportContentHash = "irrelevant-for-this-test",
            },
        });
    }

    private void StubOvertimeConfig(OvertimeBasis basis, decimal tier1AfterHours)
    {
        _overtimeConfigResolver.ResolveAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>())
            .Returns(new OvertimeSurchargeConfig
            {
                Basis = basis,
                Tiers = new List<OvertimeTierConfig>
                {
                    new(tier1AfterHours, 0.25m, SurchargeType.Overtime1),
                    new(tier1AfterHours + 4m, 0.5m, SurchargeType.Overtime2),
                },
            });
    }

    // Honours the requested range like the real provider, so the evaluator's query boundaries (week
    // attribution, window edges) are actually exercised instead of every stubbed day leaking through.
    private void StubWorkHours(params (DateOnly Date, decimal Hours)[] days)
    {
        _workHoursProvider
            .GetWorkHoursByDayAsync(ClientId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>())
            .Returns(callInfo =>
            {
                var start = callInfo.ArgAt<DateOnly>(1);
                var end = callInfo.ArgAt<DateOnly>(2);
                return (IReadOnlyDictionary<DateOnly, decimal>)days
                    .Where(d => d.Date >= start && d.Date <= end)
                    .ToDictionary(d => d.Date, d => d.Hours);
            });
    }

    // Day (2026-03-15) is a Sunday; with Monday-based weeks the March window's first attributed week
    // starts on Monday 2026-03-02 (the week containing March 1st starts on February 23rd and therefore
    // belongs to the February window).
    private static readonly DateOnly MondayInMarch = new(2026, 3, 9);

    [Test]
    public async Task EvaluateAsync_OvertimeWeekBasisUnderCap_ReturnsNoNotification()
    {
        StubOvertimeRule(capHours: 10m);
        StubOvertimeConfig(OvertimeBasis.Week, tier1AfterHours: 40m);
        StubWorkHours(
            (MondayInMarch, 10m),
            (MondayInMarch.AddDays(1), 10m),
            (MondayInMarch.AddDays(2), 10m),
            (MondayInMarch.AddDays(3), 10m),
            (MondayInMarch.AddDays(4), 8m));

        var result = await _evaluator.EvaluateAsync(ClientId, "Jane Doe", Day);

        // Week total 48h -> 8h overtime, cap 10h.
        result.ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_OvertimeWeekBasisOverCap_ReturnsWarningWithOvertimeHours()
    {
        StubOvertimeRule(capHours: 10m);
        StubOvertimeConfig(OvertimeBasis.Week, tier1AfterHours: 40m);
        StubWorkHours(
            (MondayInMarch, 12m),
            (MondayInMarch.AddDays(1), 12m),
            (MondayInMarch.AddDays(2), 12m),
            (MondayInMarch.AddDays(3), 12m),
            (MondayInMarch.AddDays(4), 5m));

        var result = await _evaluator.EvaluateAsync(ClientId, "Jane Doe", Day);

        // Week total 53h -> 13h overtime over the 40h threshold, cap 10h.
        result.Count.ShouldBe(1);
        result[0].Type.ShouldBe(ScheduleValidationType.Warning);
        result[0].Comment.ShouldBe(ScheduleValidationKeys.PeriodCapOvertime);
        result[0].ClientId.ShouldBe(ClientId);
        result[0].CommentParams.ShouldContainKeyAndValue("actualHours", "13.0");
        result[0].CommentParams.ShouldContainKeyAndValue("capHours", "10");
        result[0].CommentParams.ShouldContainKeyAndValue("period", "Month");
        result[0].CommentParams.ShouldNotContainKey("windowWeeks");
        result[0].CommentParams.ShouldNotContainKey(ComplianceRuleNames.EnforcementRuleParamKey);
    }

    [Test]
    public async Task EvaluateAsync_OvertimeDayBasis_SumsOnlyDaysAboveThreshold()
    {
        StubOvertimeRule(capHours: 3m);
        StubOvertimeConfig(OvertimeBasis.Day, tier1AfterHours: 8m);
        StubWorkHours(
            (new DateOnly(2026, 3, 10), 10m),
            (new DateOnly(2026, 3, 11), 7m),
            (new DateOnly(2026, 3, 12), 10.5m));

        var result = await _evaluator.EvaluateAsync(ClientId, "Jane Doe", Day);

        // Overtime = (10-8) + 0 + (10.5-8) = 4.5h; the 7h day must not subtract from the sum.
        result.Count.ShouldBe(1);
        result[0].Comment.ShouldBe(ScheduleValidationKeys.PeriodCapOvertime);
        result[0].CommentParams.ShouldContainKeyAndValue("actualHours", "4.5");
    }

    [Test]
    public async Task EvaluateAsync_OvertimeNoTiersWithContractThreshold_FallsBackToWeeklyBasis()
    {
        StubOvertimeRule(capHours: 4m);
        _contractDataProvider
            .GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData { OvertimeThreshold = 40m });
        StubWorkHours(
            (MondayInMarch, 9m),
            (MondayInMarch.AddDays(1), 9m),
            (MondayInMarch.AddDays(2), 9m),
            (MondayInMarch.AddDays(3), 9m),
            (MondayInMarch.AddDays(4), 9m));

        var result = await _evaluator.EvaluateAsync(ClientId, "Jane Doe", Day);

        // 45h in one week -> 5h over the weekly 40h threshold. A (wrong) day-basis interpretation of the
        // contract threshold would yield zero overtime (no single day exceeds 40h).
        result.Count.ShouldBe(1);
        result[0].CommentParams.ShouldContainKeyAndValue("actualHours", "5.0");
    }

    [Test]
    public async Task EvaluateAsync_OvertimeUndefined_SkipsRuleWithoutNotification()
    {
        StubOvertimeRule(capHours: 1m);
        StubWorkHours((new DateOnly(2026, 3, 10), 100m));

        var result = await _evaluator.EvaluateAsync(ClientId, "Jane Doe", Day);

        // No tier ladder and OvertimeThreshold 0: overtime is undefined, never "threshold 0 = all hours".
        result.ShouldBeEmpty();
        await _workHoursProvider.DidNotReceiveWithAnyArgs()
            .GetWorkHoursByDayAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>());
    }

    [Test]
    public async Task EvaluatePlannedAsync_OvertimePlannedDeltaPushesOverCap_ReturnsWarning()
    {
        StubOvertimeRule(capHours: 2m);
        StubOvertimeConfig(OvertimeBasis.Day, tier1AfterHours: 8m);
        StubWorkHours((Day, 5m));

        var result = await _evaluator.EvaluatePlannedAsync(
            ClientId,
            "Jane Doe",
            new List<(DateOnly Date, decimal Hours)> { (Day, 6m) });

        // 5h persisted + 6h planned = 11h on the day -> 3h overtime, cap 2h.
        result.Count.ShouldBe(1);
        result[0].Type.ShouldBe(ScheduleValidationType.Warning);
        result[0].CommentParams.ShouldContainKeyAndValue("actualHours", "3.0");
    }

    [Test]
    public async Task EvaluateAsync_OvertimeWeekStartingInPreviousMonth_NotAttributedToMonthWindow()
    {
        StubOvertimeRule(capHours: 5m);
        StubOvertimeConfig(OvertimeBasis.Week, tier1AfterHours: 40m);
        // March 1st 2026 is a Sunday of the week that STARTS on February 23rd: the whole week belongs to
        // the February window, so its 60h (20h overtime if miscounted) must not appear in March.
        StubWorkHours((new DateOnly(2026, 3, 1), 60m));

        var result = await _evaluator.EvaluateAsync(ClientId, "Jane Doe", Day);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_OvertimeCustomWeeksTrailingWindow_DetectsBreachAndReportsWindowWeeks()
    {
        StubOvertimeRule(capHours: 25m, period: PeriodCapPeriod.CustomWeeks, customPeriodWeeks: 4);
        StubOvertimeConfig(OvertimeBasis.Day, tier1AfterHours: 8m);
        var days = Enumerable.Range(0, 10)
            .Select(i => (new DateOnly(2026, 3, 2).AddDays(i), 11m))
            .Append((new DateOnly(2026, 2, 10), 20m))
            .ToArray();
        StubWorkHours(days);

        var result = await _evaluator.EvaluateAsync(ClientId, "Jane Doe", Day);

        // Trailing window [2026-02-16, 2026-03-15]: ten 11h days -> 30h overtime, cap 25h. The 20h day
        // on February 10th lies before the window and must not count.
        result.Count.ShouldBe(1);
        result[0].Comment.ShouldBe(ScheduleValidationKeys.PeriodCapOvertimeWindow);
        result[0].CommentParams.ShouldContainKeyAndValue("actualHours", "30.0");
        result[0].CommentParams.ShouldContainKeyAndValue("capHours", "25");
        result[0].CommentParams.ShouldNotContainKey("period");
        result[0].CommentParams.ShouldContainKeyAndValue("windowWeeks", "4");
    }

    [Test]
    public async Task EvaluateAsync_OvertimeOverCapWithBlockEnforcement_ReturnsError()
    {
        StubOvertimeRule(capHours: 3m);
        StubOvertimeConfig(OvertimeBasis.Day, tier1AfterHours: 8m);
        StubWorkHours((new DateOnly(2026, 3, 10), 13m));
        _enforcementResolver.GetModeAsync(ComplianceRuleNames.PeriodCap).Returns(RuleEnforcementMode.Block);

        var result = await _evaluator.EvaluateAsync(ClientId, "Jane Doe", Day);

        result.Count.ShouldBe(1);
        result[0].Type.ShouldBe(ScheduleValidationType.Error);
        result[0].CommentParams.ShouldContainKeyAndValue(ComplianceRuleNames.EnforcementRuleParamKey, ComplianceRuleNames.PeriodCap);
    }
}
