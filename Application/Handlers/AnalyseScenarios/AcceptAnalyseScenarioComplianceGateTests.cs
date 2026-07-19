// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.AnalyseScenarios;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.AnalyseScenarios;

[TestFixture]
public class AcceptAnalyseScenarioComplianceGateTests
{
    private static readonly DateOnly FromDate = new(2026, 5, 1);
    private static readonly DateOnly UntilDate = new(2026, 5, 31);

    private IAnalyseScenarioRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IWorkSofteningRepository _softeningRepository = null!;
    private IScenarioComplianceService _complianceService = null!;
    private ISupervisorOverrideAuthorizer _overrideAuthorizer = null!;
    private IScheduleTimelineService _timelineService = null!;
    private AcceptAnalyseScenarioCommandHandler _handler = null!;

    private AnalyseScenario _scenario = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IAnalyseScenarioRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _softeningRepository = Substitute.For<IWorkSofteningRepository>();
        _complianceService = Substitute.For<IScenarioComplianceService>();
        _overrideAuthorizer = Substitute.For<ISupervisorOverrideAuthorizer>();
        _timelineService = Substitute.For<IScheduleTimelineService>();

        _scenario = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Token = Guid.NewGuid(),
            GroupId = Guid.NewGuid(),
            FromDate = FromDate,
            UntilDate = UntilDate,
            Status = AnalyseScenarioStatus.Active,
        };
        _repository.Get(_scenario.Id).Returns(_scenario);

        _handler = new AcceptAnalyseScenarioCommandHandler(
            _repository,
            _scenarioService,
            _unitOfWork,
            _softeningRepository,
            _complianceService,
            _overrideAuthorizer,
            _timelineService,
            Substitute.For<IHttpContextAccessor>(),
            Substitute.For<ILogger<AcceptAnalyseScenarioCommandHandler>>());
    }

    private void SetReport(int blockingCount, int warningCount = 0)
    {
        var blocking = Enumerable.Range(0, blockingCount)
            .Select(_ => new PeriodIssueDto
            {
                Date = FromDate,
                ClientId = Guid.NewGuid(),
                Severity = ScheduleValidationType.Error,
                Code = "PeriodCap",
                MessageKey = "schedule.error-list.period-cap",
                MessageParams = new Dictionary<string, string>
                {
                    [ComplianceRuleNames.EnforcementRuleParamKey] = ComplianceRuleNames.PeriodCap,
                },
            })
            .ToList();
        var warnings = Enumerable.Range(0, warningCount)
            .Select(_ => new PeriodIssueDto
            {
                Date = FromDate,
                ClientId = Guid.NewGuid(),
                Severity = ScheduleValidationType.Warning,
                Code = "PeriodCap",
                MessageKey = "schedule.error-list.period-cap",
            })
            .ToList();
        var newIssues = blocking.Concat(warnings).ToList();
        _complianceService
            .EvaluateAsync(FromDate, UntilDate, _scenario.GroupId, _scenario.Token, Arg.Any<CancellationToken>())
            .Returns(new ScenarioComplianceReport(newIssues, blocking));
    }

    [Test]
    public async Task BlockingIssues_WithoutAuthorizedOverride_ThrowConflict_AndMutateNothing()
    {
        SetReport(blockingCount: 2);
        _overrideAuthorizer.IsAuthorizedAsync(false).Returns(false);

        Func<Task> act = async () => await _handler.Handle(
            new AcceptAnalyseScenarioCommand(_scenario.Id), CancellationToken.None);

        var ex = await Should.ThrowAsync<ConflictException>(act);
        ex.Message.ShouldContain("2");
        ex.Message.ShouldContain(ComplianceRuleNames.PeriodCap);

        await _scenarioService.DidNotReceive().SoftDeleteRealScheduleDataAsync(
            Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        await _scenarioService.DidNotReceive().PromoteScenarioWorksAsync(
            Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
        _timelineService.DidNotReceive().QueueRangeCheck(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>());
        _scenario.Status.ShouldBe(AnalyseScenarioStatus.Active);
    }

    [Test]
    public async Task BlockingIssues_WithAuthorizedOverride_PromoteRuns()
    {
        SetReport(blockingCount: 1);
        _overrideAuthorizer.IsAuthorizedAsync(true).Returns(true);

        var result = await _handler.Handle(
            new AcceptAnalyseScenarioCommand(_scenario.Id, OverrideBlock: true), CancellationToken.None);

        result.ShouldBeTrue();
        await _overrideAuthorizer.Received(1).IsAuthorizedAsync(true);
        await _scenarioService.Received(1).SoftDeleteRealScheduleDataAsync(
            _scenario.GroupId, _scenario.Token, FromDate, UntilDate, Arg.Any<CancellationToken>());
        await _scenarioService.Received(1).PromoteScenarioWorksAsync(
            _scenario.Token, FromDate, UntilDate, Arg.Any<CancellationToken>());
        _scenario.Status.ShouldBe(AnalyseScenarioStatus.Accepted);
    }

    [Test]
    public async Task OnlyWarningsAndNewIssues_WithoutBlocking_PromoteRuns_WithoutConsultingAuthorizer()
    {
        SetReport(blockingCount: 0, warningCount: 3);

        var result = await _handler.Handle(
            new AcceptAnalyseScenarioCommand(_scenario.Id), CancellationToken.None);

        result.ShouldBeTrue();
        await _overrideAuthorizer.DidNotReceive().IsAuthorizedAsync(Arg.Any<bool>());
        await _scenarioService.Received(1).PromoteScenarioWorksAsync(
            _scenario.Token, FromDate, UntilDate, Arg.Any<CancellationToken>());
        _scenario.Status.ShouldBe(AnalyseScenarioStatus.Accepted);
    }

    [Test]
    public async Task SuccessfulPromote_QueuesRealPlanRangeCheck_WithNullToken()
    {
        SetReport(blockingCount: 0);

        await _handler.Handle(new AcceptAnalyseScenarioCommand(_scenario.Id), CancellationToken.None);

        Received.InOrder(() =>
        {
            _unitOfWork.CompleteAsync();
            _timelineService.QueueRangeCheck(FromDate, UntilDate, null);
        });
    }

    [Test]
    public async Task ComplianceGate_RunsBeforeAnyMutation_EvenWhenPassing()
    {
        SetReport(blockingCount: 0);

        await _handler.Handle(new AcceptAnalyseScenarioCommand(_scenario.Id), CancellationToken.None);

        Received.InOrder(() =>
        {
            _complianceService.EvaluateAsync(FromDate, UntilDate, _scenario.GroupId, _scenario.Token, Arg.Any<CancellationToken>());
            _scenarioService.SoftDeleteRealScheduleDataAsync(
                Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        });
    }
}
