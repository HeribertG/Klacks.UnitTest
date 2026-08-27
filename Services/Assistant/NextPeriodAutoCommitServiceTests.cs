// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for NextPeriodAutoCommitService.CommitCompletedChainAsync — the FullyAutonomous
/// commit gate: zero new compliance issues accepts the scenario and reports the commit, one or
/// more issues (or a refused accept) leaves the scenario a draft and reports the block.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.DTOs.Schedules.AutoWizard;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Interfaces.Schedules.AutoWizard;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class NextPeriodAutoCommitServiceTests
{
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid ScenarioId = Guid.NewGuid();
    private static readonly Guid ScenarioToken = Guid.NewGuid();
    private static readonly DateOnly PeriodStart = new(2026, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2026, 2, 28);
    private const string GroupName = "Bern";

    private IScenarioComplianceService _complianceService = null!;
    private IMediator _mediator = null!;
    private IAgentConditionLedgerService _ledgerService = null!;
    private IAgentTriggerService _triggerService = null!;
    private NextPeriodAutoCommitService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _complianceService = Substitute.For<IScenarioComplianceService>();
        _mediator = Substitute.For<IMediator>();
        _ledgerService = Substitute.For<IAgentConditionLedgerService>();
        _triggerService = Substitute.For<IAgentTriggerService>();

        _ledgerService.UpsertDetectedAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((new AgentCondition { Id = Guid.NewGuid(), Status = AgentConditionStatus.Detected }, true));

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IScenarioComplianceService)).Returns(_complianceService);
        provider.GetService(typeof(IMediator)).Returns(_mediator);
        provider.GetService(typeof(IAgentConditionLedgerService)).Returns(_ledgerService);
        provider.GetService(typeof(IAgentTriggerService)).Returns(_triggerService);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _sut = new NextPeriodAutoCommitService(
            Substitute.For<IAutoWizardJobRunner>(),
            new JobTerminalStateCache<AutoWizardJobResultDto>(
                scopeFactory, NullLogger<JobTerminalStateCache<AutoWizardJobResultDto>>.Instance),
            scopeFactory,
            NullLogger<NextPeriodAutoCommitService>.Instance);
    }

    private void StubCompliance(params PeriodIssueDto[] newIssues)
    {
        _complianceService.EvaluateAsync(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ScenarioComplianceReport(newIssues.ToList(), new List<PeriodIssueDto>()));
    }

    private Task CommitAsync() =>
        _sut.CommitCompletedChainAsync(ScenarioId, ScenarioToken, GroupId, GroupName, PeriodStart, PeriodEnd);

    [Test]
    public async Task CommitCompletedChain_ZeroNewIssues_AcceptsScenarioAndReportsCommit()
    {
        StubCompliance();
        _mediator.Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>()).Returns(true);

        await CommitAsync();

        await _mediator.Received(1).Send(
            Arg.Is<AcceptAnalyseScenarioCommand>(command => command.ScenarioId == ScenarioId && !command.OverrideBlock),
            Arg.Any<CancellationToken>());
        await _triggerService.Received(1).OnEventAsync(
            Arg.Any<NextPeriodPlanCommittedTriggerEvent>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CommitCompletedChain_AnyNewIssue_LeavesDraftAndReportsBlock()
    {
        StubCompliance(new PeriodIssueDto { ClientId = Guid.NewGuid(), Code = "MIN_REST" });

        await CommitAsync();

        await _mediator.DidNotReceive().Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>());
        await _triggerService.Received(1).OnEventAsync(
            Arg.Is<NextPeriodAutoCommitBlockedTriggerEvent>(triggerEvent => triggerEvent.NewIssueCount == 1),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CommitCompletedChain_AcceptGateRefuses_ReportsBlockInsteadOfCommit()
    {
        StubCompliance();
        _mediator.Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new ConflictException("blocked"));

        await CommitAsync();

        await _triggerService.Received(1).OnEventAsync(
            Arg.Any<NextPeriodAutoCommitBlockedTriggerEvent>(), Arg.Any<CancellationToken>());
        await _triggerService.DidNotReceive().OnEventAsync(
            Arg.Any<NextPeriodPlanCommittedTriggerEvent>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CommitCompletedChain_EveryOutcome_WritesALedgerRow()
    {
        StubCompliance(new PeriodIssueDto { ClientId = Guid.NewGuid(), Code = "MAX_HOURS" });

        await CommitAsync();

        await _ledgerService.Received(1).UpsertDetectedAsync(
            AgentTriggerKinds.NextPeriodSchedulingDue,
            Arg.Any<string>(), Arg.Any<Guid?>(), GroupId,
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
