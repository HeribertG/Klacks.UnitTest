// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for PeriodCapEvaluator. Fixed-period mode (K5 stage 1, TotalHours scope): no notification
/// while the persisted-plus-planned total stays at or under the cap, a Warning once it exceeds the cap
/// under Warn enforcement, and an Error for the same breach once the PeriodCap compliance rule is set to
/// Block. Rolling-average mode (K6): same warn/block escalation for the average-over-window comparison,
/// plus the employment-start clamp that prevents padding a short history with non-existent zero weeks.
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

        _evaluator = new PeriodCapEvaluator(_ruleRepository, _periodHoursService, _enforcementResolver, _membershipStartResolver);
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

    private void StubRollingRule(int windowWeeks, decimal maxAverageWeeklyHours)
    {
        _ruleRepository.GetAllActiveAsync().Returns(new List<PeriodCapRule>
        {
            new()
            {
                Id = Guid.NewGuid(),
                RollingWindowWeeks = windowWeeks,
                MaxAverageWeeklyHours = maxAverageWeeklyHours,
                ImportSourceKey = $"region-setup:compliance.periodCaps:rolling:{windowWeeks}w",
                ImportContentHash = "irrelevant-for-this-test",
            },
        });
    }

    private void StubPersistedHours(decimal hours)
    {
        _periodHoursService
            .CalculatePeriodHoursAsync(ClientId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>())
            .Returns(new PeriodHoursResource { Hours = hours, Surcharges = 0m, GuaranteedHours = 0m });
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
}
