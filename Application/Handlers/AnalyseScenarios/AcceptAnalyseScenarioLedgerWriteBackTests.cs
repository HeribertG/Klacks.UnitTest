// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the condition-ledger write-back on scenario acceptance (Etappe 6, B1): a scenario Klacksy
/// prepared as a remediation moves its finding from Prepared to Executed and records who released it.
/// The failure-safety tests are the point of the file - by the time this write-back runs the scenario has
/// already been promoted into the real schedule, so a ledger problem must never surface as a failed
/// accept.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.AnalyseScenarios;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace Klacks.UnitTest.Application.Handlers.AnalyseScenarios;

[TestFixture]
public class AcceptAnalyseScenarioLedgerWriteBackTests
{
    private static readonly DateOnly FromDate = new(2026, 5, 1);
    private static readonly DateOnly UntilDate = new(2026, 5, 31);
    private static readonly Guid AcceptingUserId = new("672f77e8-e479-4422-8781-84d218377fb3");

    private IAnalyseScenarioRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IWorkSofteningRepository _softeningRepository = null!;
    private IScenarioComplianceService _complianceService = null!;
    private ISupervisorOverrideAuthorizer _overrideAuthorizer = null!;
    private IScheduleTimelineService _timelineService = null!;
    private IAgentConditionRepository _conditionRepository = null!;
    private IAgentConditionLedgerService _ledgerService = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
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
        _conditionRepository = Substitute.For<IAgentConditionRepository>();
        _ledgerService = Substitute.For<IAgentConditionLedgerService>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();

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

        _complianceService
            .EvaluateAsync(FromDate, UntilDate, _scenario.GroupId, _scenario.Token, Arg.Any<CancellationToken>())
            .Returns(new ScenarioComplianceReport([], []));

        _handler = new AcceptAnalyseScenarioCommandHandler(
            _repository,
            _scenarioService,
            _unitOfWork,
            _softeningRepository,
            _complianceService,
            _overrideAuthorizer,
            _timelineService,
            _conditionRepository,
            _ledgerService,
            _httpContextAccessor,
            Substitute.For<ILogger<AcceptAnalyseScenarioCommandHandler>>());
    }

    private AgentCondition GivenPreparedCondition()
    {
        var condition = new AgentCondition
        {
            Id = Guid.NewGuid(),
            TriggerKind = "empty_container",
            Fingerprint = "empty_container:x",
            Status = AgentConditionStatus.Prepared,
            ScenarioId = _scenario.Id,
        };

        _conditionRepository.FindByScenarioIdAsync(_scenario.Id, Arg.Any<CancellationToken>()).Returns(condition);
        return condition;
    }

    private void GivenAuthenticatedUser(string claimValue)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, claimValue)])),
        };

        _httpContextAccessor.HttpContext.Returns(context);
    }

    [Test]
    public async Task AcceptingAPreparedRemediation_MovesTheFindingToExecutedAndRecordsWhoReleasedIt()
    {
        var condition = GivenPreparedCondition();
        GivenAuthenticatedUser(AcceptingUserId.ToString());
        _ledgerService.TryTransitionAsync(
            Arg.Any<Guid>(),
            Arg.Any<AgentConditionStatus>(),
            Arg.Any<AgentConditionStatus>(),
            Arg.Any<Guid?>(),
            Arg.Any<string?>(),
            Arg.Any<AgentConditionTransitionFields?>(),
            Arg.Any<CancellationToken>()).Returns(true);

        var result = await _handler.Handle(
            new AcceptAnalyseScenarioCommand(_scenario.Id), CancellationToken.None);

        result.ShouldBeTrue();
        await _ledgerService.Received(1).TryTransitionAsync(
            condition.Id,
            AgentConditionStatus.Prepared,
            AgentConditionStatus.Executed,
            AcceptingUserId,
            Arg.Is<string?>(detail => detail != null && detail.Contains(_scenario.Id.ToString())),
            Arg.Is<AgentConditionTransitionFields?>(fields =>
                fields != null
                && fields.ApprovedByUserId == AcceptingUserId
                && fields.HandlingKind == AgentConditionHandlingKind.Executed),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The required failure-safety proof: the ledger throws, and the accept still succeeds. The plan has
    /// been promoted to the real schedule by the time this runs, so failing the request would tell the
    /// user their accept did not happen when it did.
    /// </summary>
    [Test]
    public async Task ALedgerFailure_DoesNotFailTheAccept()
    {
        GivenPreparedCondition();
        GivenAuthenticatedUser(AcceptingUserId.ToString());
        _ledgerService.TryTransitionAsync(
            Arg.Any<Guid>(),
            Arg.Any<AgentConditionStatus>(),
            Arg.Any<AgentConditionStatus>(),
            Arg.Any<Guid?>(),
            Arg.Any<string?>(),
            Arg.Any<AgentConditionTransitionFields?>(),
            Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("ledger is down"));

        var result = await _handler.Handle(
            new AcceptAnalyseScenarioCommand(_scenario.Id), CancellationToken.None);

        result.ShouldBeTrue();
        await _scenarioService.Received(1).PromoteScenarioWorksAsync(
            _scenario.Token, FromDate, UntilDate, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task ALookupFailureOnTheConditionRepository_DoesNotFailTheAccept()
    {
        _conditionRepository.FindByScenarioIdAsync(_scenario.Id, Arg.Any<CancellationToken>())
            .Returns<Task<AgentCondition?>>(_ => throw new InvalidOperationException("ledger is down"));

        var result = await _handler.Handle(
            new AcceptAnalyseScenarioCommand(_scenario.Id), CancellationToken.None);

        result.ShouldBeTrue();
    }

    /// <summary>
    /// A lost compare-and-swap - another planner rejected the finding first, or the tick resolved it - is
    /// an ordinary outcome, not an error, and must not surface to the accepting user either.
    /// </summary>
    [Test]
    public async Task ALostTransition_DoesNotFailTheAccept()
    {
        GivenPreparedCondition();
        GivenAuthenticatedUser(AcceptingUserId.ToString());
        _ledgerService.TryTransitionAsync(
            Arg.Any<Guid>(),
            Arg.Any<AgentConditionStatus>(),
            Arg.Any<AgentConditionStatus>(),
            Arg.Any<Guid?>(),
            Arg.Any<string?>(),
            Arg.Any<AgentConditionTransitionFields?>(),
            Arg.Any<CancellationToken>()).Returns(false);

        var result = await _handler.Handle(
            new AcceptAnalyseScenarioCommand(_scenario.Id), CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Test]
    public async Task AHumanAuthoredScenarioWithNoCondition_TouchesTheLedgerAtAll()
    {
        _conditionRepository.FindByScenarioIdAsync(_scenario.Id, Arg.Any<CancellationToken>())
            .Returns((AgentCondition?)null);

        var result = await _handler.Handle(
            new AcceptAnalyseScenarioCommand(_scenario.Id), CancellationToken.None);

        result.ShouldBeTrue();
        await _ledgerService.DidNotReceive().TryTransitionAsync(
            Arg.Any<Guid>(),
            Arg.Any<AgentConditionStatus>(),
            Arg.Any<AgentConditionStatus>(),
            Arg.Any<Guid?>(),
            Arg.Any<string?>(),
            Arg.Any<AgentConditionTransitionFields?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An acceptance outside an HTTP request, or with an identity claim that is not a Guid, still records
    /// the execution - just without an author. Losing the whole write-back over a missing name would be
    /// the worse trade.
    /// </summary>
    [Test]
    public async Task AnUnparseableIdentityClaim_StillRecordsTheExecutionWithoutAnAuthor()
    {
        var condition = GivenPreparedCondition();
        GivenAuthenticatedUser("not-a-guid");
        _ledgerService.TryTransitionAsync(
            Arg.Any<Guid>(),
            Arg.Any<AgentConditionStatus>(),
            Arg.Any<AgentConditionStatus>(),
            Arg.Any<Guid?>(),
            Arg.Any<string?>(),
            Arg.Any<AgentConditionTransitionFields?>(),
            Arg.Any<CancellationToken>()).Returns(true);

        await _handler.Handle(new AcceptAnalyseScenarioCommand(_scenario.Id), CancellationToken.None);

        await _ledgerService.Received(1).TryTransitionAsync(
            condition.Id,
            AgentConditionStatus.Prepared,
            AgentConditionStatus.Executed,
            null,
            Arg.Any<string?>(),
            Arg.Is<AgentConditionTransitionFields?>(fields => fields != null && fields.ApprovedByUserId == null),
            Arg.Any<CancellationToken>());
    }
}
