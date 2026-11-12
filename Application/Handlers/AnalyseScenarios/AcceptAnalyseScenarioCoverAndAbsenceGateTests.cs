// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the two accept refusals no override lifts: a stale scenario whose source chain starts at a cover of an absence
/// built on the real plan (the staleness says RootIsRealPlanCover: the cover itself, or a scenario built on it) is refused
/// as not overridable with the prefill read from the scenario's own token, while a chain with another root (a cover on an
/// Autofill scenario) and every other scenario keep the overridable warning; and an accept that would delete a real absence
/// the scenario does not carry is refused for every scenario, with OverrideStale, OverrideBlock and an acknowledgement
/// all set. Both refusals come before the overridable stale warning and before anything is mutated.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.AnalyseScenarios;
using Klacks.Api.Application.Interfaces.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Handlers.AnalyseScenarios;

[TestFixture]
public class AcceptAnalyseScenarioCoverAndAbsenceGateTests
{
    private static readonly DateOnly FromDate = new(2026, 5, 1);
    private static readonly DateOnly UntilDate = new(2026, 5, 31);
    private static readonly DateTime LastChange = new(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid AbsentClientId = Guid.NewGuid();
    private static readonly Guid SickAbsenceId = Guid.NewGuid();

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
        _overrideAuthorizer.IsAuthorizedAsync(Arg.Any<bool>()).Returns(true);

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
            .EvaluateAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ScenarioComplianceReport([], []));
        _scenarioService.FindCoverRedoPresetAsync(_scenario.Token, Arg.Any<CancellationToken>())
            .Returns(new CoverRedoPreset(AbsentClientId, SickAbsenceId, new DateOnly(2026, 5, 12), new DateOnly(2026, 5, 13)));

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
            Substitute.For<IPeriodHoursService>(),
            Substitute.For<IWorkNotificationService>(),
            Substitute.For<IEscalationChainService>(),
            TestGroupWriteVisibility.UnrestrictedGroups(),
            Substitute.For<ILogger<AcceptAnalyseScenarioCommandHandler>>());
    }

    [Test]
    public async Task StaleCoverOnTheRealPlan_WithEveryOverride_IsRefusedAsNotOverridable_AndMutatesNothing()
    {
        _scenario.Origin = AnalyseScenarioOrigin.AbsenceCover;
        SetStale(changedEntries: 2, rootIsRealPlanCover: true);

        var ex = await Should.ThrowAsync<ScenarioStaleException>(() => _handler.Handle(
            new AcceptAnalyseScenarioCommand(
                _scenario.Id,
                OverrideBlock: true,
                OverrideStale: true,
                AcknowledgedStaleness: new StaleScenarioOverwriteAcknowledgement(2, LastChange)),
            CancellationToken.None));

        ex.Overridable.ShouldBeFalse();
        ex.ConflictCode.ShouldBe(ScenarioStaleException.ErrorCode);
        ex.ConflictDetails![ScenarioStaleException.OverridableField].ShouldBe(false);
        ex.ConflictDetails[ScenarioStaleException.ChangedEntryCountField].ShouldBe(2);
        ex.ConflictDetails[ScenarioStaleException.CoverClientIdField].ShouldBe(AbsentClientId);
        ex.ConflictDetails[ScenarioStaleException.CoverAbsenceIdField].ShouldBe(SickAbsenceId);
        ex.ConflictDetails[ScenarioStaleException.CoverFromDateField].ShouldBe(new DateOnly(2026, 5, 12));
        ex.ConflictDetails[ScenarioStaleException.CoverUntilDateField].ShouldBe(new DateOnly(2026, 5, 13));
        await AssertNothingMutatedAsync();
    }

    [Test]
    public async Task StaleCoverOnTheRealPlan_WithoutAPrefill_StillCarriesTheNotOverridableFields()
    {
        _scenario.Origin = AnalyseScenarioOrigin.AbsenceCover;
        SetStale(changedEntries: 1, rootIsRealPlanCover: true);
        _scenarioService.FindCoverRedoPresetAsync(_scenario.Token, Arg.Any<CancellationToken>()).Returns(CoverRedoPreset.Empty);

        var ex = await Should.ThrowAsync<ScenarioStaleException>(() => _handler.Handle(
            new AcceptAnalyseScenarioCommand(_scenario.Id, OverrideStale: true), CancellationToken.None));

        ex.Overridable.ShouldBeFalse();
        ex.ConflictDetails!.ShouldContainKey(ScenarioStaleException.CoverClientIdField);
        ex.ConflictDetails![ScenarioStaleException.CoverClientIdField].ShouldBeNull();
        ex.ConflictDetails![ScenarioStaleException.CoverFromDateField].ShouldBeNull();
        await AssertNothingMutatedAsync();
    }

    [Test]
    public async Task CoverOnTheRealPlan_NotStale_IsAcceptedWithoutOverride()
    {
        _scenario.Origin = AnalyseScenarioOrigin.AbsenceCover;

        var result = await _handler.Handle(new AcceptAnalyseScenarioCommand(_scenario.Id), CancellationToken.None);

        result.Accepted.ShouldBeTrue();
        await _scenarioService.DidNotReceive().FindCoverRedoPresetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        _scenario.Status.ShouldBe(AnalyseScenarioStatus.Accepted);
    }

    [Test]
    public async Task StaleCoverOnAnAutofillSourceScenario_WithOverrideStale_IsAccepted()
    {
        _scenario.Origin = AnalyseScenarioOrigin.AbsenceCover;
        _scenario.SourceToken = Guid.NewGuid();
        SetStale(changedEntries: 1);

        var result = await _handler.Handle(
            new AcceptAnalyseScenarioCommand(_scenario.Id, OverrideStale: true), CancellationToken.None);

        result.Accepted.ShouldBeTrue();
        await _scenarioService.Received(1).SoftDeleteRealScheduleDataAsync(
            _scenario.GroupId, _scenario.Token, FromDate, UntilDate, Arg.Any<CancellationToken>());
        _scenario.Status.ShouldBe(AnalyseScenarioStatus.Accepted);
    }

    [Test]
    public async Task StaleCoverOnAnAutofillSourceScenario_WithoutOverride_IsRefusedAsOverridable()
    {
        _scenario.Origin = AnalyseScenarioOrigin.AbsenceCover;
        _scenario.SourceToken = Guid.NewGuid();
        SetStale(changedEntries: 1);

        var ex = await Should.ThrowAsync<ScenarioStaleException>(() => _handler.Handle(
            new AcceptAnalyseScenarioCommand(_scenario.Id), CancellationToken.None));

        ex.Overridable.ShouldBeTrue();
        ex.ConflictDetails![ScenarioStaleException.OverridableField].ShouldBe(true);
        ex.ConflictDetails.ShouldNotContainKey(ScenarioStaleException.CoverClientIdField);
        await AssertNothingMutatedAsync();
    }

    [Test]
    public async Task StaleScenarioBuiltOnARealPlanCover_WithEveryOverride_IsRefusedAsNotOverridable_WithItsOwnPrefill()
    {
        _scenario.SourceToken = Guid.NewGuid();
        SetStale(changedEntries: 2, rootIsRealPlanCover: true);

        var ex = await Should.ThrowAsync<ScenarioStaleException>(() => _handler.Handle(
            new AcceptAnalyseScenarioCommand(
                _scenario.Id,
                OverrideBlock: true,
                OverrideStale: true,
                AcknowledgedStaleness: new StaleScenarioOverwriteAcknowledgement(2, LastChange)),
            CancellationToken.None));

        ex.Overridable.ShouldBeFalse();
        ex.ConflictDetails![ScenarioStaleException.CoverClientIdField].ShouldBe(AbsentClientId);
        await _scenarioService.Received(1).FindCoverRedoPresetAsync(_scenario.Token, Arg.Any<CancellationToken>());
        await AssertNothingMutatedAsync();
    }

    [Test]
    public async Task StaleScenarioOfAnotherOrigin_WithOverrideStale_IsAcceptedAsToday()
    {
        SetStale(changedEntries: 1);

        var result = await _handler.Handle(
            new AcceptAnalyseScenarioCommand(_scenario.Id, OverrideStale: true), CancellationToken.None);

        result.Accepted.ShouldBeTrue();
        await _scenarioService.DidNotReceive().FindCoverRedoPresetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ScenarioThatDropsARealAbsence_WithEveryOverride_IsRefused_AndMutatesNothing()
    {
        SetStale(changedEntries: 1);
        SetDropsRealAbsence();

        var ex = await Should.ThrowAsync<ScenarioDropsRealAbsenceException>(() => _handler.Handle(
            new AcceptAnalyseScenarioCommand(
                _scenario.Id,
                OverrideBlock: true,
                OverrideStale: true,
                AcknowledgedStaleness: new StaleScenarioOverwriteAcknowledgement(1, LastChange)),
            CancellationToken.None));

        ex.ConflictCode.ShouldBe(ScenarioDropsRealAbsenceException.ErrorCode);
        ex.ConflictDetails![ScenarioDropsRealAbsenceException.DroppedAbsenceCountField].ShouldBe(1);
        ex.ConflictDetails[ScenarioDropsRealAbsenceException.OverridableField].ShouldBe(false);
        await AssertNothingMutatedAsync();
        await _complianceService.DidNotReceive().EvaluateAsync(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ScenarioThatDropsARealAbsence_NotStale_IsRefused()
    {
        SetDropsRealAbsence();

        await Should.ThrowAsync<ScenarioDropsRealAbsenceException>(() => _handler.Handle(
            new AcceptAnalyseScenarioCommand(_scenario.Id), CancellationToken.None));

        await AssertNothingMutatedAsync();
    }

    [Test]
    public async Task StaleScenarioThatDropsARealAbsence_WithoutOverride_IsRefusedForTheAbsence_NotWithTheOverridableWarning()
    {
        SetStale(changedEntries: 3);
        SetDropsRealAbsence();

        await Should.ThrowAsync<ScenarioDropsRealAbsenceException>(() => _handler.Handle(
            new AcceptAnalyseScenarioCommand(_scenario.Id), CancellationToken.None));

        await AssertNothingMutatedAsync();
    }

    [Test]
    public async Task StaleCoverOnTheRealPlanThatAlsoDropsARealAbsence_IsRefusedAsNotOverridableStale()
    {
        _scenario.Origin = AnalyseScenarioOrigin.AbsenceCover;
        SetStale(changedEntries: 3, rootIsRealPlanCover: true);
        SetDropsRealAbsence();

        var ex = await Should.ThrowAsync<ScenarioStaleException>(() => _handler.Handle(
            new AcceptAnalyseScenarioCommand(_scenario.Id, OverrideStale: true), CancellationToken.None));

        ex.Overridable.ShouldBeFalse();
        await AssertNothingMutatedAsync();
    }

    [Test]
    public async Task EveryAccept_RunsTheAbsenceCheckOnTheScenario()
    {
        await _handler.Handle(new AcceptAnalyseScenarioCommand(_scenario.Id), CancellationToken.None);

        await _scenarioService.Received(1).EnsureNoRealAbsenceDroppedAsync(_scenario, Arg.Any<CancellationToken>());
    }

    private void SetStale(int changedEntries, bool rootIsRealPlanCover = false)
        => _scenarioService
            .FindRealPlanChangesSinceCreationAsync(_scenario, Arg.Any<CancellationToken>())
            .Returns(new ScenarioStaleness(changedEntries, LastChange, Array.Empty<string>(), rootIsRealPlanCover));

    private void SetDropsRealAbsence()
        => _scenarioService
            .EnsureNoRealAbsenceDroppedAsync(_scenario, Arg.Any<CancellationToken>())
            .Returns(_ => throw new ScenarioDropsRealAbsenceException(
                1, new[] { new DroppedRealAbsence(AbsentClientId, "Elsa Muster", new DateOnly(2026, 5, 12), SickAbsenceId) }));

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
