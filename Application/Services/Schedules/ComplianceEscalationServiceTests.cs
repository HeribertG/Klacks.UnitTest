// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ComplianceEscalationService: the timeline-key-to-rule-name map is complete, a
/// Block-mode rule escalates its Warning to an Error tagged with the enforcement-rule param, Warn
/// mode leaves entries untouched, unknown comments are never escalated, and the PeriodIssueDto
/// overload applies the identical semantics on Severity/MessageParams.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class ComplianceEscalationServiceTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 4, 6);

    private IComplianceEnforcementResolver _enforcementResolver = null!;
    private ComplianceEscalationService _service = null!;

    [SetUp]
    public void Setup()
    {
        _enforcementResolver = Substitute.For<IComplianceEnforcementResolver>();
        _enforcementResolver.GetModeAsync(Arg.Any<string>()).Returns(RuleEnforcementMode.Warn);
        _service = new ComplianceEscalationService(_enforcementResolver);
    }

    private static ScheduleValidationNotificationDto Warning(string comment) => new()
    {
        Type = ScheduleValidationType.Warning,
        ClientId = ClientId,
        Date = Day,
        Comment = comment,
        CommentParams = new Dictionary<string, string> { ["actualHours"] = "12" },
    };

    private static PeriodIssueDto WarningIssue(string messageKey) => new()
    {
        Date = Day,
        ClientId = ClientId,
        Severity = ScheduleValidationType.Warning,
        Code = "Timeline",
        MessageKey = messageKey,
        MessageParams = new Dictionary<string, string> { ["actualHours"] = "12" },
    };

    [TestCase(ScheduleValidationKeys.RestViolation, ComplianceRuleNames.MinRestHours)]
    [TestCase(ScheduleValidationKeys.Overtime, ComplianceRuleNames.MaxDailyHours)]
    [TestCase(ScheduleValidationKeys.ConsecutiveDays, ComplianceRuleNames.MaxConsecutiveDays)]
    [TestCase(ScheduleValidationKeys.WeeklyOvertime, ComplianceRuleNames.MaxWeeklyHours)]
    [TestCase(ScheduleValidationKeys.MinRestDays, ComplianceRuleNames.MinRestDays)]
    public async Task MappingTable_IsComplete_EachTimelineKeyEscalatesToItsRule(string comment, string expectedRule)
    {
        _enforcementResolver.GetModeAsync(expectedRule).Returns(RuleEnforcementMode.Block);
        var conflicts = new List<ScheduleValidationNotificationDto> { Warning(comment) };

        await _service.EscalateBlockedViolationsAsync(conflicts);

        var escalated = conflicts.Single();
        escalated.Type.ShouldBe(ScheduleValidationType.Error);
        escalated.CommentParams[ComplianceRuleNames.EnforcementRuleParamKey].ShouldBe(expectedRule);
    }

    [Test]
    public async Task BlockMode_EscalatesWarningToError_KeepsExistingParams()
    {
        _enforcementResolver.GetModeAsync(ComplianceRuleNames.MinRestHours).Returns(RuleEnforcementMode.Block);
        var conflicts = new List<ScheduleValidationNotificationDto> { Warning(ScheduleValidationKeys.RestViolation) };

        await _service.EscalateBlockedViolationsAsync(conflicts);

        var escalated = conflicts.Single();
        escalated.Type.ShouldBe(ScheduleValidationType.Error);
        escalated.CommentParams["actualHours"].ShouldBe("12");
        escalated.CommentParams[ComplianceRuleNames.EnforcementRuleParamKey].ShouldBe(ComplianceRuleNames.MinRestHours);
    }

    [Test]
    public async Task WarnMode_LeavesWarningUntouched()
    {
        var conflicts = new List<ScheduleValidationNotificationDto> { Warning(ScheduleValidationKeys.RestViolation) };

        await _service.EscalateBlockedViolationsAsync(conflicts);

        var entry = conflicts.Single();
        entry.Type.ShouldBe(ScheduleValidationType.Warning);
        entry.CommentParams.ShouldNotContainKey(ComplianceRuleNames.EnforcementRuleParamKey);
        await _enforcementResolver.Received(1).GetModeAsync(ComplianceRuleNames.MinRestHours);
    }

    [Test]
    public async Task UnknownComment_IsNeverEscalated_EvenWhenEverythingIsBlockMode()
    {
        _enforcementResolver.GetModeAsync(Arg.Any<string>()).Returns(RuleEnforcementMode.Block);
        var conflicts = new List<ScheduleValidationNotificationDto> { Warning(ScheduleValidationKeys.Collision) };

        await _service.EscalateBlockedViolationsAsync(conflicts);

        conflicts.Single().Type.ShouldBe(ScheduleValidationType.Warning);
        await _enforcementResolver.DidNotReceive().GetModeAsync(Arg.Any<string>());
    }

    [Test]
    public async Task ExistingError_IsLeftUntouched_NoDoubleEscalation()
    {
        _enforcementResolver.GetModeAsync(Arg.Any<string>()).Returns(RuleEnforcementMode.Block);
        var error = Warning(ScheduleValidationKeys.RestViolation) with { Type = ScheduleValidationType.Error };
        var conflicts = new List<ScheduleValidationNotificationDto> { error };

        await _service.EscalateBlockedViolationsAsync(conflicts);

        conflicts.Single().ShouldBeSameAs(error);
        conflicts.Single().CommentParams.ShouldNotContainKey(ComplianceRuleNames.EnforcementRuleParamKey);
    }

    [Test]
    public async Task IssueOverload_BlockMode_EscalatesSeverity_AndTagsMessageParams()
    {
        _enforcementResolver.GetModeAsync(ComplianceRuleNames.MaxWeeklyHours).Returns(RuleEnforcementMode.Block);
        var issue = WarningIssue(ScheduleValidationKeys.WeeklyOvertime);

        await _service.EscalateBlockedIssuesAsync([issue]);

        issue.Severity.ShouldBe(ScheduleValidationType.Error);
        issue.MessageParams[ComplianceRuleNames.EnforcementRuleParamKey].ShouldBe(ComplianceRuleNames.MaxWeeklyHours);
        issue.MessageParams["actualHours"].ShouldBe("12");
    }

    [Test]
    public async Task IssueOverload_WarnModeAndUnknownKey_StayUntouched()
    {
        var warnIssue = WarningIssue(ScheduleValidationKeys.WeeklyOvertime);
        var unknownIssue = WarningIssue(ScheduleValidationKeys.Collision);

        await _service.EscalateBlockedIssuesAsync([warnIssue, unknownIssue]);

        warnIssue.Severity.ShouldBe(ScheduleValidationType.Warning);
        warnIssue.MessageParams.ShouldNotContainKey(ComplianceRuleNames.EnforcementRuleParamKey);
        unknownIssue.Severity.ShouldBe(ScheduleValidationType.Warning);
        unknownIssue.MessageParams.ShouldNotContainKey(ComplianceRuleNames.EnforcementRuleParamKey);
    }
}
