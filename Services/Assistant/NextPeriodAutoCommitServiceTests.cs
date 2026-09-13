// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for NextPeriodAutoCommitService - the FullyAutonomous commit gate. Two properties are
/// under test that the service exists for: nothing is committed unless every gate still says yes at
/// commit time (compliance, kill switch, autonomy level), and NO failure path is silent - each one
/// posts a NextPeriodAutoCommitBlockedTriggerEvent carrying its own reason, so the planners can never
/// be left believing a period was committed while it is still a draft.
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
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class NextPeriodAutoCommitServiceTests
{
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid ScenarioId = Guid.NewGuid();
    private static readonly Guid ScenarioToken = Guid.NewGuid();
    private static readonly Guid JobId = Guid.NewGuid();
    private static readonly Guid DecidingAdminId = Guid.Parse("3f1c9a52-0000-0000-0000-000000000001");
    private static readonly DateOnly PeriodStart = new(2026, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2026, 2, 28);
    private const string GroupName = "Bern";

    private IScenarioComplianceService _complianceService = null!;
    private IMediator _mediator = null!;
    private IAgentConditionLedgerService _ledgerService = null!;
    private IAgentTriggerService _triggerService = null!;
    private IProactiveGovernanceResolver _governanceResolver = null!;
    private INextPeriodAutonomyResolver _autonomyResolver = null!;
    private IAutoWizardJobRunner _jobRunner = null!;
    private IServiceScopeFactory _scopeFactory = null!;
    private SettableTimeProvider _timeProvider = null!;
    private NextPeriodAutoCommitService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _complianceService = Substitute.For<IScenarioComplianceService>();
        _mediator = Substitute.For<IMediator>();
        _ledgerService = Substitute.For<IAgentConditionLedgerService>();
        _triggerService = Substitute.For<IAgentTriggerService>();
        _governanceResolver = Substitute.For<IProactiveGovernanceResolver>();
        _governanceResolver.IsKillSwitchActiveAsync(Arg.Any<CancellationToken>()).Returns(false);
        _autonomyResolver = Substitute.For<INextPeriodAutonomyResolver>();
        _autonomyResolver.ResolveAsync(Arg.Any<CancellationToken>())
            .Returns(new NextPeriodAutonomyDecision(AutonomyLevel.FullyAutonomous, DecidingAdminId));
        _jobRunner = Substitute.For<IAutoWizardJobRunner>();
        _timeProvider = new SettableTimeProvider(new DateTime(2026, 1, 28, 9, 0, 0, DateTimeKind.Utc));

        _ledgerService.UpsertDetectedAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((new AgentCondition { Id = Guid.NewGuid(), Status = AgentConditionStatus.Detected }, true));

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IScenarioComplianceService)).Returns(_complianceService);
        provider.GetService(typeof(IMediator)).Returns(_mediator);
        provider.GetService(typeof(IAgentConditionLedgerService)).Returns(_ledgerService);
        provider.GetService(typeof(IAgentTriggerService)).Returns(_triggerService);
        provider.GetService(typeof(IProactiveGovernanceResolver)).Returns(_governanceResolver);
        provider.GetService(typeof(INextPeriodAutonomyResolver)).Returns(_autonomyResolver);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        _scopeFactory = Substitute.For<IServiceScopeFactory>();
        _scopeFactory.CreateScope().Returns(scope);

        _sut = CreateSut(NullLogger<NextPeriodAutoCommitService>.Instance);
    }

    private NextPeriodAutoCommitService CreateSut(ILogger<NextPeriodAutoCommitService> logger) =>
        new(_jobRunner,
            new JobTerminalStateCache<AutoWizardJobResultDto>(
                _scopeFactory, NullLogger<JobTerminalStateCache<AutoWizardJobResultDto>>.Instance),
            _scopeFactory,
            Substitute.For<IHostApplicationLifetime>(),
            _timeProvider,
            logger);

    private void StubCompliance(params PeriodIssueDto[] newIssues)
    {
        _complianceService.EvaluateAsync(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ScenarioComplianceReport(newIssues.ToList(), new List<PeriodIssueDto>()));
    }

    private Task CommitAsync() =>
        _sut.CommitCompletedChainAsync(
            ScenarioId, ScenarioToken, GroupId, GroupName, PeriodStart, PeriodEnd, CancellationToken.None);

    /// <summary>
    /// The single blocked event that was dispatched, read back from the recorded calls. Arg.Do cannot do
    /// this: its callback fires while the call is made, not while a Received() assertion replays it.
    /// </summary>
    private NextPeriodAutoCommitBlockedTriggerEvent CapturedBlockedEvent()
    {
        var captured = _triggerService.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IAgentTriggerService.OnEventAsync))
            .Select(call => call.GetArguments().FirstOrDefault())
            .OfType<NextPeriodAutoCommitBlockedTriggerEvent>()
            .ToList();

        Assert.That(captured, Has.Count.EqualTo(1), "Exactly one blocked event must be dispatched.");
        return captured[0];
    }

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
    public async Task CommitCompletedChain_SuccessfulCommit_IsAttributedToTheDecidingAdmin()
    {
        StubCompliance();
        _mediator.Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>()).Returns(true);

        await CommitAsync();

        await _mediator.Received(1).Send(
            Arg.Is<AcceptAnalyseScenarioCommand>(command => command.ActingUserId == DecidingAdminId),
            Arg.Any<CancellationToken>());
        await _ledgerService.Received(1).TryTransitionAsync(
            Arg.Any<Guid>(),
            AgentConditionStatus.Detected,
            AgentConditionStatus.Reported,
            DecidingAdminId,
            Arg.Any<string?>(),
            Arg.Is<AgentConditionTransitionFields?>(fields => fields != null && fields.ApprovedByUserId == DecidingAdminId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CommitCompletedChain_BlockedOutcome_RecordsNoApprovingUser()
    {
        StubCompliance(new PeriodIssueDto { ClientId = Guid.NewGuid(), Code = "MIN_REST" });

        await CommitAsync();

        await _ledgerService.Received(1).TryTransitionAsync(
            Arg.Any<Guid>(),
            AgentConditionStatus.Detected,
            AgentConditionStatus.Reported,
            null,
            Arg.Any<string?>(),
            null,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CommitCompletedChain_KillSwitchActive_WithholdsAcceptAndReportsIt()
    {
        _governanceResolver.IsKillSwitchActiveAsync(Arg.Any<CancellationToken>()).Returns(true);
        StubCompliance();
        _mediator.Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>()).Returns(true);

        await CommitAsync();

        await _complianceService.DidNotReceiveWithAnyArgs().EvaluateAsync(
            default, default, default, default, default);
        await _mediator.DidNotReceiveWithAnyArgs().Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>());
        var blocked = CapturedBlockedEvent();
        Assert.That(blocked.Reason, Is.EqualTo(NextPeriodAutoCommitBlockReason.KillSwitch));
    }

    [Test]
    public async Task CommitCompletedChain_AutonomyLoweredDuringTheWatch_WithholdsAcceptAndReportsIt()
    {
        StubCompliance();
        _autonomyResolver.ResolveAsync(Arg.Any<CancellationToken>())
            .Returns(new NextPeriodAutonomyDecision(AutonomyLevel.Autonomous, DecidingAdminId));

        await CommitAsync();

        await _mediator.DidNotReceiveWithAnyArgs().Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>());
        var blocked = CapturedBlockedEvent();
        Assert.That(blocked.Reason, Is.EqualTo(NextPeriodAutoCommitBlockReason.AutonomyLowered));
    }

    [Test]
    public async Task CommitCompletedChain_AnyNewIssue_LeavesDraftAndReportsBlock()
    {
        StubCompliance(new PeriodIssueDto { ClientId = Guid.NewGuid(), Code = "MIN_REST" });

        await CommitAsync();

        await _mediator.DidNotReceive().Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>());
        var blocked = CapturedBlockedEvent();
        Assert.Multiple(() =>
        {
            Assert.That(blocked.Reason, Is.EqualTo(NextPeriodAutoCommitBlockReason.NewViolations));
            Assert.That(blocked.NewIssueCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task CommitCompletedChain_AcceptReturnsFalse_ReportsRefused()
    {
        StubCompliance();
        _mediator.Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>()).Returns(false);

        await CommitAsync();

        var blocked = CapturedBlockedEvent();
        Assert.That(blocked.Reason, Is.EqualTo(NextPeriodAutoCommitBlockReason.Refused));
        await _triggerService.DidNotReceive().OnEventAsync(
            Arg.Any<NextPeriodPlanCommittedTriggerEvent>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CommitCompletedChain_AcceptGateRefuses_ReportsConflictInsteadOfCommit()
    {
        StubCompliance();
        _mediator.Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new ConflictException("blocked"));

        await CommitAsync();

        var blocked = CapturedBlockedEvent();
        Assert.That(blocked.Reason, Is.EqualTo(NextPeriodAutoCommitBlockReason.Conflict));
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

    [Test]
    public async Task WatchAndCommit_ChainNeverFinishes_ReportsTimeout()
    {
        // The job never leaves the registry, and the clock jumps past the watch window on the first
        // poll, so the timeout is reached without the test ever waiting a real poll interval.
        _jobRunner.IsRunning(JobId).Returns(_ =>
        {
            _timeProvider.Now = _timeProvider.Now
                .AddMinutes(NextPeriodScheduling.AutoCommitWatchTimeoutMinutes + 1);
            return true;
        });

        await _sut.WatchAndCommitAsync(JobId, GroupId, GroupName, PeriodStart, PeriodEnd, CancellationToken.None);

        var blocked = CapturedBlockedEvent();
        Assert.That(blocked.Reason, Is.EqualTo(NextPeriodAutoCommitBlockReason.Timeout));
    }

    [Test]
    public async Task WatchAndCommit_ChainEndedWithoutACommittableResult_ReportsNotCommittable()
    {
        _jobRunner.IsRunning(JobId).Returns(false);

        await _sut.WatchAndCommitAsync(JobId, GroupId, GroupName, PeriodStart, PeriodEnd, CancellationToken.None);

        var blocked = CapturedBlockedEvent();
        Assert.Multiple(() =>
        {
            Assert.That(blocked.Reason, Is.EqualTo(NextPeriodAutoCommitBlockReason.NotCommittable));
            Assert.That(blocked.ScenarioId, Is.Null, "No final scenario exists yet on this path.");
        });
        await _mediator.DidNotReceiveWithAnyArgs().Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WatchAndCommit_ShutdownDuringTheWait_IsInformationNotAnError()
    {
        var logger = Substitute.For<ILogger<NextPeriodAutoCommitService>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        _sut = CreateSut(logger);
        _jobRunner.IsRunning(JobId).Returns(true);
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await _sut.WatchAndCommitAsync(JobId, GroupId, GroupName, PeriodStart, PeriodEnd, stopping.Token);

        logger.DidNotReceive().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
        logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
        await _triggerService.DidNotReceiveWithAnyArgs().OnEventAsync(
            Arg.Any<IAgentTriggerEvent>(), Arg.Any<CancellationToken>());
    }
}
