// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the staleness gate of AcceptAnalyseScenarioCommandHandler: a scenario whose original plan changed after it
/// was created is refused with a SCENARIO_STALE conflict before anything is mutated, unless the caller acknowledged it
/// with OverrideStale. The two overrides are independent: OverrideBlock never passes the staleness gate and
/// OverrideStale never passes the compliance block, so one confirmation can never skip both warnings.
/// </summary>

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
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Handlers.AnalyseScenarios;

[TestFixture]
public class AcceptAnalyseScenarioStaleGateTests
{
    private static readonly DateOnly FromDate = new(2026, 5, 1);
    private static readonly DateOnly UntilDate = new(2026, 5, 31);
    private static readonly DateTime LastChange = new(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

    private IAnalyseScenarioRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IScenarioComplianceService _complianceService = null!;
    private ISupervisorOverrideAuthorizer _overrideAuthorizer = null!;
    private AcceptAnalyseScenarioCommandHandler _handler = null!;
    private AnalyseScenario _scenario = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IAnalyseScenarioRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _complianceService = Substitute.For<IScenarioComplianceService>();
        _overrideAuthorizer = Substitute.For<ISupervisorOverrideAuthorizer>();

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
        SetBlockingIssues(0);

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
    public async Task StaleScenario_WithoutOverride_IsRefusedWithStaleCode_AndMutatesNothing()
    {
        SetStale(changedEntries: 3, "Scenario B");

        var ex = await Should.ThrowAsync<ScenarioStaleException>(() => _handler.Handle(
            new AcceptAnalyseScenarioCommand(_scenario.Id), CancellationToken.None));

        ex.ConflictCode.ShouldBe(ScenarioStaleException.ErrorCode);
        ex.ConflictDetails.ShouldNotBeNull();
        ex.ConflictDetails![ScenarioStaleException.ChangedEntryCountField].ShouldBe(3);
        ex.ConflictDetails[ScenarioStaleException.LastChangeUtcField].ShouldBe(LastChange);
        ex.ConflictDetails[ScenarioStaleException.AcceptedScenarioNamesField]
            .ShouldBeAssignableTo<IReadOnlyList<string>>()!.ShouldBe(new[] { "Scenario B" });
        await AssertNothingMutatedAsync();
        await _complianceService.DidNotReceive().EvaluateAsync(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StaleScenario_WithOverrideStale_IsAccepted()
    {
        SetStale(changedEntries: 1);

        var result = await _handler.Handle(
            new AcceptAnalyseScenarioCommand(_scenario.Id, OverrideStale: true), CancellationToken.None);

        result.Accepted.ShouldBeTrue();
        await _scenarioService.Received(1).SoftDeleteRealScheduleDataAsync(
            _scenario.GroupId, _scenario.Token, FromDate, UntilDate, Arg.Any<CancellationToken>());
        await _scenarioService.Received(1).PromoteScenarioWorksAsync(
            _scenario.Token, FromDate, UntilDate, Arg.Any<CancellationToken>());
        _scenario.Status.ShouldBe(AnalyseScenarioStatus.Accepted);
    }

    [Test]
    public async Task StaleScenario_WithOverrideBlockOnly_IsStillRefusedAsStale()
    {
        SetStale(changedEntries: 1);
        _overrideAuthorizer.IsAuthorizedAsync(true).Returns(true);

        await Should.ThrowAsync<ScenarioStaleException>(() => _handler.Handle(
            new AcceptAnalyseScenarioCommand(_scenario.Id, OverrideBlock: true), CancellationToken.None));

        await AssertNothingMutatedAsync();
    }

    [Test]
    public async Task StaleAndBlocked_WithOverrideStaleOnly_IsRefusedByTheComplianceBlock()
    {
        SetStale(changedEntries: 1);
        SetBlockingIssues(1);
        _overrideAuthorizer.IsAuthorizedAsync(false).Returns(false);

        var ex = await Should.ThrowAsync<ConflictException>(() => _handler.Handle(
            new AcceptAnalyseScenarioCommand(_scenario.Id, OverrideStale: true), CancellationToken.None));

        ex.ShouldNotBeOfType<ScenarioStaleException>();
        ex.ConflictCode.ShouldBeNull();
        await AssertNothingMutatedAsync();
    }

    [Test]
    public async Task StaleAndBlocked_WithBothOverrides_IsAccepted()
    {
        SetStale(changedEntries: 1);
        SetBlockingIssues(1);
        _overrideAuthorizer.IsAuthorizedAsync(true).Returns(true);

        var result = await _handler.Handle(
            new AcceptAnalyseScenarioCommand(_scenario.Id, OverrideBlock: true, OverrideStale: true), CancellationToken.None);

        result.Accepted.ShouldBeTrue();
        _scenario.Status.ShouldBe(AnalyseScenarioStatus.Accepted);
    }

    [Test]
    public async Task OverrideWithTheAcknowledgedStaleness_IsAccepted()
    {
        SetStale(changedEntries: 3);

        var result = await _handler.Handle(
            new AcceptAnalyseScenarioCommand(
                _scenario.Id,
                OverrideStale: true,
                AcknowledgedStaleness: new StaleScenarioOverwriteAcknowledgement(3, LastChange)),
            CancellationToken.None);

        result.Accepted.ShouldBeTrue();
        _scenario.Status.ShouldBe(AnalyseScenarioStatus.Accepted);
    }

    [Test]
    public async Task OverrideAcknowledgingFewerChangesThanNow_IsRefusedAsStaleAgain()
    {
        SetStale(changedEntries: 5);

        var ex = await Should.ThrowAsync<ScenarioStaleException>(() => _handler.Handle(
            new AcceptAnalyseScenarioCommand(
                _scenario.Id,
                OverrideStale: true,
                AcknowledgedStaleness: new StaleScenarioOverwriteAcknowledgement(3, LastChange)),
            CancellationToken.None));

        ex.Staleness.ChangedEntryCount.ShouldBe(5);
        await AssertNothingMutatedAsync();
    }

    [Test]
    public async Task OverrideAcknowledgingAnOlderLastChange_IsRefusedAsStaleAgain()
    {
        SetStale(changedEntries: 3);

        await Should.ThrowAsync<ScenarioStaleException>(() => _handler.Handle(
            new AcceptAnalyseScenarioCommand(
                _scenario.Id,
                OverrideStale: true,
                AcknowledgedStaleness: new StaleScenarioOverwriteAcknowledgement(3, LastChange.AddMinutes(-5))),
            CancellationToken.None));

        await AssertNothingMutatedAsync();
    }

    [Test]
    public async Task UnchangedOriginal_IsAcceptedWithoutAnyOverride()
    {
        var result = await _handler.Handle(new AcceptAnalyseScenarioCommand(_scenario.Id), CancellationToken.None);

        result.Accepted.ShouldBeTrue();
        await _scenarioService.Received(1).FindRealPlanChangesSinceCreationAsync(_scenario, Arg.Any<CancellationToken>());
        _scenario.Status.ShouldBe(AnalyseScenarioStatus.Accepted);
    }

    [Test]
    public async Task Accept_ReportsTheEntriesNotTakenOverOnLockedDays()
    {
        _scenarioService
            .PromoteScenarioWorksAsync(_scenario.Token, FromDate, UntilDate, Arg.Any<CancellationToken>())
            .Returns(new ScenarioAcceptLockSkips(2, 1, 3));

        var result = await _handler.Handle(new AcceptAnalyseScenarioCommand(_scenario.Id), CancellationToken.None);

        result.ShouldBe(new AcceptAnalyseScenarioResult(true, 2, 1, 3));
        result.SkippedLockedTotal.ShouldBe(6);
    }

    private void SetStale(int changedEntries, params string[] acceptedScenarioNames)
        => _scenarioService
            .FindRealPlanChangesSinceCreationAsync(_scenario, Arg.Any<CancellationToken>())
            .Returns(new ScenarioStaleness(changedEntries, LastChange, acceptedScenarioNames));

    private void SetBlockingIssues(int count)
    {
        var blocking = Enumerable.Range(0, count)
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
        _complianceService
            .EvaluateAsync(FromDate, UntilDate, _scenario.GroupId, _scenario.Token, Arg.Any<CancellationToken>())
            .Returns(new ScenarioComplianceReport(blocking, blocking));
    }

    private async Task AssertNothingMutatedAsync()
    {
        await _scenarioService.DidNotReceive().SoftDeleteRealScheduleDataAsync(
            Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        await _scenarioService.DidNotReceive().PromoteScenarioWorksAsync(
            Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
        _scenario.Status.ShouldBe(AnalyseScenarioStatus.Active);
    }
}
