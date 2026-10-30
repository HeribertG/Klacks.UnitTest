// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for evaluate_compliance_enforcement_change: the skill asks the period issue query once for the window
/// of four weeks around the company's today without a group, counts warnings and enforced errors per rule, reports
/// the cap of the issue list as lower bounds, refuses an unknown rule before any query, relays the advisor's
/// recommendation and carries no name of a person. Default scenario: company day 2026-09-02, supervisor override on,
/// every rule in warn.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.ComplianceEnforcement;
using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Queries.PeriodClosing;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class EvaluateComplianceEnforcementChangeSkillTests
{
    private const string PersonName = "Anna Muster";

    private static readonly DateOnly Today = new(2026, 9, 2);
    private static readonly DateOnly WindowStart = new(2026, 8, 5);
    private static readonly DateOnly WindowEnd = new(2026, 9, 30);

    private IMediator _mediator = null!;
    private IComplianceEnforcementResolver _resolver = null!;
    private ICompanyClock _clock = null!;
    private List<PeriodIssueDto> _issues = null!;

    [SetUp]
    public void Setup()
    {
        _issues = [];
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<GetPeriodIssuesQuery>(), Arg.Any<CancellationToken>()).Returns(_ => _issues);
        _resolver = Substitute.For<IComplianceEnforcementResolver>();
        _resolver.GetModeAsync(Arg.Any<string>()).Returns(RuleEnforcementMode.Warn);
        _resolver.IsSupervisorOverrideAllowedAsync().Returns(true);
        _clock = Substitute.For<ICompanyClock>();
        _clock.GetTodayDateAsync(Arg.Any<CancellationToken>()).Returns(Today);
    }

    private EvaluateComplianceEnforcementChangeSkill Skill() => new(_mediator, _resolver, _clock);

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Roles.Admin }
    };

    private static PeriodIssueDto Warning(string key, DateOnly? date = null) => new()
    {
        Severity = ScheduleValidationType.Warning,
        MessageKey = key,
        Code = "ScheduleValidation",
        Date = date ?? Today,
        ClientId = Guid.NewGuid(),
        ClientName = PersonName
    };

    private static PeriodIssueDto Enforced(string key, string rule)
    {
        var issue = Warning(key);
        issue.Severity = ScheduleValidationType.Error;
        issue.MessageParams[ComplianceRuleNames.EnforcementRuleParamKey] = rule;
        return issue;
    }

    private static Dictionary<string, object> ForRule(string rule) => new() { ["rule"] = rule };

    private async Task<ComplianceEnforcementChangeEvaluation> EvaluateAsync(Dictionary<string, object>? parameters = null)
    {
        var result = await Skill().ExecuteAsync(Ctx(), parameters ?? new Dictionary<string, object>());

        result.Success.ShouldBeTrue(result.Message);
        return result.Data.ShouldBeOfType<ComplianceEnforcementChangeEvaluation>();
    }

    [Test]
    public async Task WithoutARule_EvaluatesAllTwelve_AndAsksTheIssueQueryOnceForTheWholeInstallation()
    {
        using var source = new CancellationTokenSource();
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>(), source.Token);

        var evaluation = result.Data.ShouldBeOfType<ComplianceEnforcementChangeEvaluation>();
        evaluation.Rules.Count.ShouldBe(12);
        evaluation.WindowStart.ShouldBe(WindowStart);
        evaluation.WindowEnd.ShouldBe(WindowEnd);
        await _mediator.Received(1).Send(
            Arg.Is<GetPeriodIssuesQuery>(query =>
                query.From == WindowStart && query.To == WindowEnd && query.GroupId == null),
            source.Token);
        result.Message.ShouldBe(evaluation.Recommendation);
    }

    [Test]
    public async Task WithARule_CountsOnlyItsWarnings_AndIgnoresNotesAndOtherRules()
    {
        _issues =
        [
            Warning(ScheduleValidationKeys.RestViolation),
            Warning(ScheduleValidationKeys.RestViolation),
            Warning(ScheduleValidationKeys.Overtime),
            Warning(ScheduleValidationKeys.PeriodCapApproaching),
            new PeriodIssueDto
            {
                Severity = ScheduleValidationType.Info,
                Code = PeriodIssueLimits.ScheduleNoteCode,
                MessageKey = ScheduleValidationKeys.RestViolation
            }
        ];

        var evaluation = await EvaluateAsync(ForRule("MinRestHours"));

        var effect = evaluation.Rules.Single();
        effect.Rule.ShouldBe(ComplianceRuleNames.MinRestHours);
        effect.WarningsTurningIntoErrors.ShouldBe(2);
        effect.Outcome.ShouldBe(ComplianceRuleOutcome.WarningsBecomeErrors);
        evaluation.CountsAreLowerBounds.ShouldBeFalse();
    }

    [Test]
    public async Task RuleAlreadyInBlock_ReportsItsEnforcedErrors_AndNoWarningsTurningIntoErrors()
    {
        _resolver.GetModeAsync(ComplianceRuleNames.HolidayWork).Returns(RuleEnforcementMode.Block);
        _issues =
        [
            Enforced(ScheduleValidationKeys.HolidayWork, ComplianceRuleNames.HolidayWork),
            Enforced(ScheduleValidationKeys.HolidayWork, ComplianceRuleNames.HolidayWork),
            Enforced(ScheduleValidationKeys.RestViolation, ComplianceRuleNames.MinRestHours)
        ];

        var evaluation = await EvaluateAsync(ForRule(ComplianceRuleNames.HolidayWork));

        var effect = evaluation.Rules.Single();
        effect.CurrentMode.ShouldBe(RuleEnforcementMode.Block);
        effect.Outcome.ShouldBe(ComplianceRuleOutcome.AlreadyBlocking);
        effect.ErrorsAlreadyRaised.ShouldBe(2);
        effect.WarningsTurningIntoErrors.ShouldBe(0);
    }

    [Test]
    public async Task EachRule_ReadsItsOwnMode_AndTheSupervisorOverride()
    {
        _resolver.GetModeAsync(ComplianceRuleNames.PeriodCap).Returns(RuleEnforcementMode.Block);
        _resolver.IsSupervisorOverrideAllowedAsync().Returns(false);

        var evaluation = await EvaluateAsync();

        evaluation.Rules.Single(effect => effect.Rule == ComplianceRuleNames.PeriodCap).CurrentMode
            .ShouldBe(RuleEnforcementMode.Block);
        evaluation.Rules.Count(effect => effect.CurrentMode == RuleEnforcementMode.Warn).ShouldBe(11);
        evaluation.SupervisorOverrideAllowed.ShouldBeFalse();
    }

    [Test]
    public async Task FullIssueList_IsReportedAsLowerBound_WithTheLastCoveredDay_NotesDoNotCountTowardsTheCap()
    {
        var last = new DateOnly(2026, 9, 12);
        _issues = Enumerable.Range(0, PeriodIssueLimits.MaxValidationIssues)
            .Select(index => Warning(ScheduleValidationKeys.Overtime, index == 0 ? last : last.AddDays(-index % 5)))
            .ToList();

        var evaluation = await EvaluateAsync(ForRule(ComplianceRuleNames.MaxDailyHours));

        evaluation.CountsAreLowerBounds.ShouldBeTrue();
        evaluation.CoveredUntil.ShouldBe(last);
        evaluation.Rules.Single().WarningsTurningIntoErrors.ShouldBe(PeriodIssueLimits.MaxValidationIssues);
        evaluation.Recommendation.ShouldContain($"at least {PeriodIssueLimits.MaxValidationIssues}");
    }

    [Test]
    public async Task OneBelowTheCap_PlusManyNotes_IsNotALowerBound()
    {
        _issues = Enumerable.Range(0, PeriodIssueLimits.MaxValidationIssues - 1)
            .Select(_ => Warning(ScheduleValidationKeys.Overtime))
            .Concat(Enumerable.Range(0, 20).Select(_ => new PeriodIssueDto
            {
                Severity = ScheduleValidationType.Info,
                Code = PeriodIssueLimits.ScheduleNoteCode
            }))
            .ToList();

        var evaluation = await EvaluateAsync(ForRule(ComplianceRuleNames.MaxDailyHours));

        evaluation.CountsAreLowerBounds.ShouldBeFalse();
        evaluation.CoveredUntil.ShouldBeNull();
    }

    [TestCase("planningRule")]
    [TestCase("rest time")]
    public async Task UnknownRule_IsRefusedBeforeAnyQuery_AndListsTheValidNames(string rule)
    {
        var result = await Skill().ExecuteAsync(Ctx(), ForRule(rule));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain($"Unknown rule '{rule}'");
        result.Message.ShouldContain(ComplianceRuleNames.MinRestHours);
        await _mediator.DidNotReceive().Send(Arg.Any<GetPeriodIssuesQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BlankRule_MeansAllRules()
    {
        var evaluation = await EvaluateAsync(ForRule("  "));

        evaluation.Rules.Count.ShouldBe(12);
    }

    [Test]
    public async Task Result_SerializesTheModesAsWords_NotAsNumbers()
    {
        _resolver.GetModeAsync(ComplianceRuleNames.HolidayWork).Returns(RuleEnforcementMode.Block);

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        var json = JsonSerializer.Serialize(result.Data);
        json.ShouldContain("\"CurrentMode\":\"Warn\"");
        json.ShouldContain("\"CurrentMode\":\"Block\"");
        json.ShouldNotContain("\"CurrentMode\":0");
        json.ShouldNotContain("\"CurrentMode\":1");
    }

    [Test]
    public async Task Result_CarriesNoNameOfAPerson_AndTheSkillOnlyReads()
    {
        _issues = [Warning(ScheduleValidationKeys.RestViolation), Enforced(ScheduleValidationKeys.Overtime, ComplianceRuleNames.MaxDailyHours)];

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        JsonSerializer.Serialize(result.Data).ShouldNotContain(PersonName);
        result.Message.ShouldNotContain(PersonName);
        _mediator.ReceivedCalls().Count().ShouldBe(1);
    }
}
