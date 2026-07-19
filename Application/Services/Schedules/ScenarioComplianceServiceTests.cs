// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ScenarioComplianceService over a substituted period validation loader and the REAL
/// escalation service (substituted enforcement resolver): pre-existing real-plan issues are diffed
/// away, issues only present in the scenario remain, BlockingIssues contains exactly the Error
/// entries tagged with the enforcement-rule param, escalation is applied symmetrically to both loads
/// before diffing, and both loads run untruncated.
/// </summary>

using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.Interfaces.PeriodClosing;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class ScenarioComplianceServiceTests
{
    private static readonly DateOnly From = new(2026, 4, 1);
    private static readonly DateOnly Until = new(2026, 4, 30);
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid Token = Guid.NewGuid();
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 4, 10);

    private IPeriodValidationLoader _loader = null!;
    private IComplianceEnforcementResolver _enforcementResolver = null!;
    private ScenarioComplianceService _service = null!;

    [SetUp]
    public void Setup()
    {
        _loader = Substitute.For<IPeriodValidationLoader>();
        SetIssues([], []);
        _enforcementResolver = Substitute.For<IComplianceEnforcementResolver>();
        _enforcementResolver.GetModeAsync(Arg.Any<string>()).Returns(RuleEnforcementMode.Warn);
        _service = new ScenarioComplianceService(_loader, new ComplianceEscalationService(_enforcementResolver));
    }

    private void SetIssues(List<PeriodIssueDto> scenarioIssues, List<PeriodIssueDto> baselineIssues)
    {
        _loader.LoadAsync(From, Until, GroupId, Token, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(scenarioIssues);
        _loader.LoadAsync(From, Until, GroupId, (Guid?)null, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(baselineIssues);
    }

    private static PeriodIssueDto Issue(
        string code,
        string messageKey,
        ScheduleValidationType severity = ScheduleValidationType.Warning,
        DateOnly? date = null,
        Dictionary<string, string>? messageParams = null)
        => new()
        {
            Date = date ?? Day,
            ClientId = ClientId,
            ClientName = "Anna",
            Severity = severity,
            Code = code,
            MessageKey = messageKey,
            MessageParams = messageParams ?? new Dictionary<string, string> { ["actualHours"] = "12" },
        };

    private Task<Klacks.Api.Application.DTOs.Schedules.ScenarioComplianceReport> Evaluate()
        => _service.EvaluateAsync(From, Until, GroupId, Token, CancellationToken.None);

    [Test]
    public async Task PreExistingRealIssue_IsDiffedAway()
    {
        SetIssues(
            scenarioIssues: [Issue("WeeklyOvertime", ScheduleValidationKeys.WeeklyOvertime)],
            baselineIssues: [Issue("WeeklyOvertime", ScheduleValidationKeys.WeeklyOvertime)]);

        var report = await Evaluate();

        report.NewIssues.ShouldBeEmpty();
        report.BlockingIssues.ShouldBeEmpty();
    }

    [Test]
    public async Task NewScenarioIssue_Remains()
    {
        SetIssues(
            scenarioIssues:
            [
                Issue("WeeklyOvertime", ScheduleValidationKeys.WeeklyOvertime),
                Issue("Collision", ScheduleValidationKeys.Collision, ScheduleValidationType.Error, Day.AddDays(1)),
            ],
            baselineIssues: [Issue("WeeklyOvertime", ScheduleValidationKeys.WeeklyOvertime)]);

        var report = await Evaluate();

        report.NewIssues.ShouldHaveSingleItem().Code.ShouldBe("Collision");
    }

    [Test]
    public async Task BlockingIssues_OnlyContainsErrors_TaggedWithEnforcementRule()
    {
        _enforcementResolver.GetModeAsync(ComplianceRuleNames.MinRestHours).Returns(RuleEnforcementMode.Block);
        SetIssues(
            scenarioIssues:
            [
                Issue("RestViolation", ScheduleValidationKeys.RestViolation),
                Issue("Collision", ScheduleValidationKeys.Collision, ScheduleValidationType.Error, Day.AddDays(1)),
                Issue("Overtime", ScheduleValidationKeys.Overtime, date: Day.AddDays(2)),
            ],
            baselineIssues: []);

        var report = await Evaluate();

        report.NewIssues.Count.ShouldBe(3);
        var blocking = report.BlockingIssues.ShouldHaveSingleItem();
        blocking.Code.ShouldBe("RestViolation");
        blocking.Severity.ShouldBe(ScheduleValidationType.Error);
        blocking.MessageParams[ComplianceRuleNames.EnforcementRuleParamKey].ShouldBe(ComplianceRuleNames.MinRestHours);
    }

    [Test]
    public async Task PreExistingIssue_UnderBlockMode_IsEscalatedOnBothSides_AndStillDiffedAway()
    {
        _enforcementResolver.GetModeAsync(ComplianceRuleNames.MinRestHours).Returns(RuleEnforcementMode.Block);
        SetIssues(
            scenarioIssues: [Issue("RestViolation", ScheduleValidationKeys.RestViolation)],
            baselineIssues: [Issue("RestViolation", ScheduleValidationKeys.RestViolation)]);

        var report = await Evaluate();

        report.NewIssues.ShouldBeEmpty();
        report.BlockingIssues.ShouldBeEmpty();
    }

    [Test]
    public async Task BothLoads_RunUntruncated()
    {
        await Evaluate();

        await _loader.Received(1).LoadAsync(From, Until, GroupId, Token, int.MaxValue, Arg.Any<CancellationToken>());
        await _loader.Received(1).LoadAsync(From, Until, GroupId, (Guid?)null, int.MaxValue, Arg.Any<CancellationToken>());
    }
}
