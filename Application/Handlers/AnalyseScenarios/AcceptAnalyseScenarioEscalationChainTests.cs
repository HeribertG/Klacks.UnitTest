// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Accepting a scenario that covers an absence must end the still-running "who covers this absence" call
/// lists of that scenario's breaks (the accept keeps the break, so the background sweep that supersedes chains
/// of deleted breaks would never stop them). The breaks are read BEFORE the promote clears their scenario
/// token, the chains are ended AFTER the accept is committed, and a failure while ending them never fails the
/// (already durable) accept.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.AnalyseScenarios;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.AnalyseScenarios;

[TestFixture]
public class AcceptAnalyseScenarioEscalationChainTests
{
    private static readonly DateOnly FromDate = new(2026, 11, 2);
    private static readonly DateOnly UntilDate = new(2026, 11, 8);

    private IUnitOfWork _unitOfWork = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IEscalationChainService _escalationChainService = null!;
    private AcceptAnalyseScenarioCommandHandler _handler = null!;
    private AnalyseScenario _scenario = null!;
    private List<Guid> _breakIds = null!;

    [SetUp]
    public void Setup()
    {
        var repository = Substitute.For<IAnalyseScenarioRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _escalationChainService = Substitute.For<IEscalationChainService>();
        var complianceService = Substitute.For<IScenarioComplianceService>();

        _scenario = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Token = Guid.NewGuid(),
            GroupId = Guid.NewGuid(),
            FromDate = FromDate,
            UntilDate = UntilDate,
            Status = AnalyseScenarioStatus.Active,
        };
        _breakIds = [Guid.NewGuid(), Guid.NewGuid()];

        repository.Get(_scenario.Id).Returns(_scenario);
        complianceService
            .EvaluateAsync(FromDate, UntilDate, _scenario.GroupId, _scenario.Token, Arg.Any<CancellationToken>())
            .Returns(new ScenarioComplianceReport([], []));
        _scenarioService.GetScenarioBreakIdsAsync(_scenario.Token, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<Guid>)_breakIds);

        _handler = new AcceptAnalyseScenarioCommandHandler(
            repository,
            _scenarioService,
            _unitOfWork,
            Substitute.For<IWorkSofteningRepository>(),
            complianceService,
            Substitute.For<ISupervisorOverrideAuthorizer>(),
            Substitute.For<IScheduleTimelineService>(),
            Substitute.For<IAgentConditionRepository>(),
            Substitute.For<IAgentConditionLedgerService>(),
            Substitute.For<IHttpContextAccessor>(),
            Substitute.For<IPeriodHoursService>(),
            Substitute.For<IWorkNotificationService>(),
            _escalationChainService,
            Substitute.For<ILogger<AcceptAnalyseScenarioCommandHandler>>());
    }

    [Test]
    public async Task Accept_ReadsBreaksBeforePromote_AndEndsTheirChainsAfterCommit()
    {
        var result = await _handler.Handle(new AcceptAnalyseScenarioCommand(_scenario.Id), CancellationToken.None);

        result.ShouldBeTrue();
        Received.InOrder(() =>
        {
            _scenarioService.GetScenarioBreakIdsAsync(_scenario.Token, Arg.Any<CancellationToken>());
            _scenarioService.PromoteScenarioWorksAsync(_scenario.Token, FromDate, UntilDate, Arg.Any<CancellationToken>());
            _unitOfWork.CompleteAsync();
            _escalationChainService.SupersedeAbsenceChainsForBreaksAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(_breakIds)),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task AFailureEndingTheChains_DoesNotFailTheAccept()
    {
        _escalationChainService
            .SupersedeAbsenceChainsForBreaksAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<int>>(_ => throw new InvalidOperationException("database is down"));

        var result = await _handler.Handle(new AcceptAnalyseScenarioCommand(_scenario.Id), CancellationToken.None);

        result.ShouldBeTrue();
        await _unitOfWork.Received(1).CompleteAsync();
    }
}
