// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ComplianceRuleCatalog: the rule names it accepts, the message keys each rule governs, and a guard that
/// every schedule validation key is either governed by a rule or deliberately exempt, so a new key cannot slip past the
/// enforcement advice unnoticed. The five timeline rules take their keys from ComplianceEscalationService, so they cannot drift.
/// </summary>

using System.Reflection;
using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.Services.ComplianceEnforcement;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Application.Services.ComplianceEnforcement;

[TestFixture]
public class ComplianceRuleCatalogTests
{
    private static readonly string[] ExemptKeys =
    [
        ScheduleValidationKeys.Collision,
        ScheduleValidationKeys.DayDirective,
        ScheduleValidationKeys.OnCallOverlap,
        ScheduleValidationKeys.PeriodCapApproaching,
        ScheduleValidationKeys.PeriodCapOvertimeApproaching,
        ScheduleValidationKeys.PeriodCapOvertimeWindowApproaching,
        ScheduleValidationKeys.PlanningRule,
        ScheduleValidationKeys.PlanningRuleInvalid,
        ScheduleValidationKeys.TravelTimeError,
        ScheduleValidationKeys.TravelTimeWarning,
        ScheduleValidationKeys.TravelTimeNoApiKey,
    ];

    [Test]
    public void Catalog_HasTheTwelveRulesWithModes_AndNotThePlanningRule()
    {
        ComplianceRuleCatalog.Rules.Count.ShouldBe(12);
        ComplianceRuleCatalog.Rules.Distinct().Count().ShouldBe(12);
        ComplianceRuleCatalog.Rules.ShouldNotContain(ComplianceRuleNames.PlanningRule);
    }

    [TestCase("minRestHours", "minRestHours")]
    [TestCase("  MINRESTHOURS ", "minRestHours")]
    [TestCase("HolidayWork", "holidayWork")]
    public void Normalize_AcceptsAnyCasingAndWhitespace(string raw, string expected)
    {
        ComplianceRuleCatalog.Normalize(raw).ShouldBe(expected);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    [TestCase("planningRule")]
    [TestCase("maxDailyHour")]
    public void Normalize_RefusesAnythingElse(string? raw)
    {
        ComplianceRuleCatalog.Normalize(raw).ShouldBeNull();
    }

    [TestCase(ComplianceRuleNames.MaxDailyHours, ScheduleValidationKeys.Overtime)]
    [TestCase(ComplianceRuleNames.MaxWeeklyHours, ScheduleValidationKeys.WeeklyOvertime)]
    [TestCase(ComplianceRuleNames.MinRestHours, ScheduleValidationKeys.RestViolation)]
    [TestCase(ComplianceRuleNames.MinRestDays, ScheduleValidationKeys.MinRestDays)]
    [TestCase(ComplianceRuleNames.MaxConsecutiveDays, ScheduleValidationKeys.ConsecutiveDays)]
    [TestCase(ComplianceRuleNames.RollingAverage, ScheduleValidationKeys.RollingAverage)]
    [TestCase(ComplianceRuleNames.RestDayRotation, ScheduleValidationKeys.RestDayRotation)]
    [TestCase(ComplianceRuleNames.CounterRule, ScheduleValidationKeys.CounterRule)]
    [TestCase(ComplianceRuleNames.RestrictedTimeWindow, ScheduleValidationKeys.RestrictedTimeWindow)]
    [TestCase(ComplianceRuleNames.HolidayWork, ScheduleValidationKeys.HolidayWork)]
    public void SingleKeyRule_GovernsExactlyThatKey(string rule, string key)
    {
        ComplianceRuleCatalog.IssueKeys(rule).ShouldBe([key]);
    }

    [Test]
    public void PeriodCap_GovernsTheThreeBreachKeys_ButNotTheApproachingWarnings()
    {
        var keys = ComplianceRuleCatalog.IssueKeys(ComplianceRuleNames.PeriodCap);

        keys.ShouldBe(
        [
            ScheduleValidationKeys.PeriodCap,
            ScheduleValidationKeys.PeriodCapOvertime,
            ScheduleValidationKeys.PeriodCapOvertimeWindow,
        ]);
    }

    [Test]
    public void CompensatoryRest_GovernsDueAndOverdue()
    {
        ComplianceRuleCatalog.IssueKeys(ComplianceRuleNames.CompensatoryRest)
            .ShouldBe([ScheduleValidationKeys.CompensatoryRestDue, ScheduleValidationKeys.CompensatoryRestOverdue]);
    }

    [Test]
    public void TimelineRules_TakeTheirKeysFromTheEscalationService()
    {
        foreach (var (key, rule) in ComplianceEscalationService.CommentToRuleName)
        {
            ComplianceRuleCatalog.IssueKeys(rule).ShouldContain(key);
        }
    }

    [Test]
    public void NoKeyIsGovernedByTwoRules()
    {
        var allKeys = ComplianceRuleCatalog.Rules.SelectMany(ComplianceRuleCatalog.IssueKeys).ToList();

        allKeys.Count.ShouldBe(allKeys.Distinct().Count());
    }

    [Test]
    public void EveryRuleOfTheCatalog_GovernsAtLeastOneKey_AndHasALabel()
    {
        foreach (var rule in ComplianceRuleCatalog.Rules)
        {
            ComplianceRuleCatalog.IssueKeys(rule).ShouldNotBeEmpty(rule);
            ComplianceRuleCatalog.Label(rule).ShouldNotBe(rule);
        }
    }

    [Test]
    public void EveryScheduleValidationKey_IsGovernedByARuleOrExempt()
    {
        var governed = ComplianceRuleCatalog.Rules.SelectMany(ComplianceRuleCatalog.IssueKeys).ToHashSet();
        var allKeys = typeof(ScheduleValidationKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        var undecided = allKeys.Where(key => !governed.Contains(key) && !ExemptKeys.Contains(key)).ToList();

        undecided.ShouldBeEmpty("A new schedule validation key needs a rule in ComplianceRuleCatalog or an entry in the exempt list.");
        ExemptKeys.Intersect(governed).ShouldBeEmpty();
    }

    [Test]
    public void Counter_CountsOnlyWarningsOfTheRulesKeys_AndOnlyErrorsTaggedWithTheRule()
    {
        var issues = new List<PeriodIssueDto>
        {
            Issue(Klacks.Api.Domain.Enums.ScheduleValidationType.Warning, ScheduleValidationKeys.RestViolation),
            Issue(Klacks.Api.Domain.Enums.ScheduleValidationType.Warning, ScheduleValidationKeys.RestViolation),
            Issue(Klacks.Api.Domain.Enums.ScheduleValidationType.Warning, ScheduleValidationKeys.Overtime),
            Issue(Klacks.Api.Domain.Enums.ScheduleValidationType.Error, ScheduleValidationKeys.RestViolation, ComplianceRuleNames.MinRestHours),
            Issue(Klacks.Api.Domain.Enums.ScheduleValidationType.Error, ScheduleValidationKeys.Collision),
            Issue(Klacks.Api.Domain.Enums.ScheduleValidationType.Error, ScheduleValidationKeys.Overtime, ComplianceRuleNames.MaxDailyHours),
            Issue(Klacks.Api.Domain.Enums.ScheduleValidationType.Info, ScheduleValidationKeys.RestViolation),
            Issue(Klacks.Api.Domain.Enums.ScheduleValidationType.Warning, ScheduleValidationKeys.PeriodCapApproaching),
        };

        var rest = ComplianceRuleIssueCounter.Count(ComplianceRuleNames.MinRestHours, Klacks.Api.Domain.Enums.RuleEnforcementMode.Warn, issues);
        var periodCap = ComplianceRuleIssueCounter.Count(ComplianceRuleNames.PeriodCap, Klacks.Api.Domain.Enums.RuleEnforcementMode.Warn, issues);

        rest.WarningCount.ShouldBe(2);
        rest.EnforcedErrorCount.ShouldBe(1);
        rest.CurrentMode.ShouldBe(Klacks.Api.Domain.Enums.RuleEnforcementMode.Warn);
        periodCap.WarningCount.ShouldBe(0);
        periodCap.EnforcedErrorCount.ShouldBe(0);
    }

    private static PeriodIssueDto Issue(Klacks.Api.Domain.Enums.ScheduleValidationType severity, string key, string? enforcedRule = null)
    {
        var issue = new PeriodIssueDto { Severity = severity, MessageKey = key, ClientName = "Anna Muster" };
        if (enforcedRule != null)
        {
            issue.MessageParams[ComplianceRuleNames.EnforcementRuleParamKey] = enforcedRule;
        }

        return issue;
    }
}
