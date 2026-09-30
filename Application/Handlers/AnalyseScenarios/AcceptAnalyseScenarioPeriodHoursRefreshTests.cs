// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Accepting a scenario promotes works in bulk and bypasses the per-work period-hours hooks, which left the
/// schedule row header on 00:00 after "Klacksy plant die Woche". The accept must refresh the real plan's
/// cached period hours for the accepted range AFTER the promote is committed, tell open schedules to reload
/// them, and never fail the (already durable) accept when that refresh throws.
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
public class AcceptAnalyseScenarioPeriodHoursRefreshTests
{
    private static readonly DateOnly FromDate = new(2026, 11, 2);
    private static readonly DateOnly UntilDate = new(2026, 11, 8);

    private IUnitOfWork _unitOfWork = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private IWorkNotificationService _notificationService = null!;
    private AcceptAnalyseScenarioCommandHandler _handler = null!;
    private AnalyseScenario _scenario = null!;

    [SetUp]
    public void Setup()
    {
        var repository = Substitute.For<IAnalyseScenarioRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _notificationService = Substitute.For<IWorkNotificationService>();
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
        repository.Get(_scenario.Id).Returns(_scenario);
        complianceService
            .EvaluateAsync(FromDate, UntilDate, _scenario.GroupId, _scenario.Token, Arg.Any<CancellationToken>())
            .Returns(new ScenarioComplianceReport([], []));

        _handler = new AcceptAnalyseScenarioCommandHandler(
            repository,
            Substitute.For<IAnalyseScenarioService>(),
            _unitOfWork,
            Substitute.For<IWorkSofteningRepository>(),
            complianceService,
            Substitute.For<ISupervisorOverrideAuthorizer>(),
            Substitute.For<IScheduleTimelineService>(),
            Substitute.For<IAgentConditionRepository>(),
            Substitute.For<IAgentConditionLedgerService>(),
            Substitute.For<IHttpContextAccessor>(),
            _periodHoursService,
            _notificationService,
            Substitute.For<ILogger<AcceptAnalyseScenarioCommandHandler>>());
    }

    [Test]
    public async Task Accept_RefreshesRealPeriodHoursAfterCommit_AndNotifiesOpenSchedules()
    {
        var result = await _handler.Handle(new AcceptAnalyseScenarioCommand(_scenario.Id), CancellationToken.None);

        result.ShouldBeTrue();
        Received.InOrder(() =>
        {
            _unitOfWork.CompleteAsync();
            _periodHoursService.RefreshCachedPeriodHoursAsync(FromDate, UntilDate, null);
            _notificationService.NotifyPeriodHoursRecalculated(FromDate, UntilDate, null);
        });
    }

    [Test]
    public async Task ARefreshFailure_DoesNotFailTheAccept()
    {
        _periodHoursService.RefreshCachedPeriodHoursAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>())
            .Returns<Task>(_ => throw new InvalidOperationException("database is down"));

        var result = await _handler.Handle(new AcceptAnalyseScenarioCommand(_scenario.Id), CancellationToken.None);

        result.ShouldBeTrue();
        await _unitOfWork.Received(1).CompleteAsync();
    }
}
