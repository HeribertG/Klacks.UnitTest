// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins where AcceptAnalyseScenarioCommandHandler applies the variant accept rule: after the status check and before the
/// conflict validation and every mutation. A refusal (missing right, consumed key) leaves the scenario Active and mutates
/// nothing, and neither OverrideStale nor OverrideBlock lifts it.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.AnalyseScenarios;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.AnalyseScenarios;

[TestFixture]
public class AcceptAnalyseScenarioVariantGateTests
{
    private static readonly DateOnly FromDate = new(2026, 5, 1);
    private static readonly DateOnly UntilDate = new(2026, 5, 31);

    private IAnalyseScenarioRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IScenarioComplianceService _complianceService = null!;
    private ISupervisorOverrideAuthorizer _overrideAuthorizer = null!;
    private AcceptAnalyseScenarioCommandHandler _handler = null!;
    private AnalyseScenario _scenario = null!;
    private CancellationToken _ct;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IAnalyseScenarioRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _complianceService = Substitute.For<IScenarioComplianceService>();
        _overrideAuthorizer = Substitute.For<ISupervisorOverrideAuthorizer>();
        _ct = new CancellationTokenSource().Token;

        _scenario = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Token = Guid.NewGuid(),
            GroupId = Guid.NewGuid(),
            FromDate = FromDate,
            UntilDate = UntilDate,
            Status = AnalyseScenarioStatus.Active,
            CreateTime = new DateTime(2026, 4, 20, 8, 0, 0, DateTimeKind.Utc),
        };
        _repository.Get(_scenario.Id).Returns(_scenario);
        _complianceService
            .EvaluateAsync(FromDate, UntilDate, _scenario.GroupId, _scenario.Token, Arg.Any<CancellationToken>())
            .Returns(new ScenarioComplianceReport(new List<PeriodIssueDto>(), new List<PeriodIssueDto>()));

        _handler = new AcceptAnalyseScenarioCommandHandler(
            _repository,
            _scenarioService,
            _unitOfWork,
            Substitute.For<IWorkSofteningRepository>(),
            _complianceService,
            _overrideAuthorizer,
            Substitute.For<IScheduleTimelineService>(),
            Substitute.For<IAgentConditionRepository>(),
            Substitute.For<IAgentConditionLedgerService>(),
            Substitute.For<IHttpContextAccessor>(),
            Substitute.For<Klacks.Api.Domain.Interfaces.Schedules.IPeriodHoursService>(),
            Substitute.For<IWorkNotificationService>(),
            Substitute.For<IEscalationChainService>(),
            TestGroupWriteVisibility.UnrestrictedGroups(),
            Substitute.For<ILogger<AcceptAnalyseScenarioCommandHandler>>());
    }

    [Test]
    public async Task Accept_AsksTheVariantRule_AfterTheStatusCheckAndBeforeAnyConflictCheckOrMutation()
    {
        var result = await _handler.Handle(new AcceptAnalyseScenarioCommand(_scenario.Id), _ct);

        result.Accepted.ShouldBeTrue();
        Received.InOrder(() =>
        {
            _scenarioService.EnsureVariantAcceptAllowedAsync(_scenario.Id, _scenario.Token, _ct);
            _scenarioService.ValidateNoAcceptConflictsAsync(_scenario.Token, _ct);
            _scenarioService.FindRealPlanChangesSinceCreationAsync(_scenario, _ct);
            _complianceService.EvaluateAsync(FromDate, UntilDate, _scenario.GroupId, _scenario.Token, _ct);
            _scenarioService.SoftDeleteRealScheduleDataAsync(_scenario.GroupId, _scenario.Token, FromDate, UntilDate, _ct);
            _scenarioService.PromoteScenarioWorksAsync(_scenario.Token, FromDate, UntilDate, _ct);
        });
    }

    [Test]
    public async Task Accept_CallerMayNotAcceptVariants_IsRefusedWithForbidden_AndMutatesNothing()
    {
        _scenarioService.EnsureVariantAcceptAllowedAsync(_scenario.Id, _scenario.Token, _ct)
            .Returns(Task.FromException(new ForbiddenException(ScenarioAcceptDefaults.VariantAcceptForbiddenMessage)));

        var ex = await Should.ThrowAsync<ForbiddenException>(() => _handler.Handle(
            new AcceptAnalyseScenarioCommand(_scenario.Id), _ct));

        ex.Message.ShouldBe(ScenarioAcceptDefaults.VariantAcceptForbiddenMessage);
        await AssertRefusedBeforeAnythingElseAsync();
    }

    [Test]
    public async Task Accept_ConsumedVariant_IsRefused_EvenWithBothOverrides()
    {
        _overrideAuthorizer.IsAuthorizedAsync(true).Returns(true);
        _scenarioService.EnsureVariantAcceptAllowedAsync(_scenario.Id, _scenario.Token, _ct)
            .Returns(Task.FromException(new ScenarioVariantConsumedException(new[] { "Winter keys" })));

        var ex = await Should.ThrowAsync<ScenarioVariantConsumedException>(() => _handler.Handle(
            new AcceptAnalyseScenarioCommand(_scenario.Id, OverrideBlock: true, OverrideStale: true), _ct));

        ex.ConflictCode.ShouldBe(ScenarioVariantConsumedException.ErrorCode);
        await AssertRefusedBeforeAnythingElseAsync();
    }

    [Test]
    public async Task Accept_ScenarioNoLongerActive_IsRefusedAsNotActive_BeforeTheVariantRuleRuns()
    {
        _scenario.Status = AnalyseScenarioStatus.Accepted;

        await Should.ThrowAsync<ScenarioNotActiveException>(() => _handler.Handle(
            new AcceptAnalyseScenarioCommand(_scenario.Id), _ct));

        await _scenarioService.DidNotReceive().EnsureVariantAcceptAllowedAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private async Task AssertRefusedBeforeAnythingElseAsync()
    {
        await _scenarioService.DidNotReceive().ValidateNoAcceptConflictsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _scenarioService.DidNotReceive().SoftDeleteRealScheduleDataAsync(
            Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        await _scenarioService.DidNotReceive().PromoteScenarioWorksAsync(
            Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        await _complianceService.DidNotReceive().EvaluateAsync(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
        _scenario.Status.ShouldBe(AnalyseScenarioStatus.Active);
    }
}
