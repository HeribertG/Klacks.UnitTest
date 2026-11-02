// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins where AcceptAnalyseScenarioCommandHandler numbers the trees of promoted variant (key) shifts: the root ids are read
/// before the promote clears the key tokens, the numbering runs with a second commit right after the accept commit and
/// before the steps that commit on their own. A failing numbering leaves the accept standing, drops its half-done values
/// from the tracker and does not stop the steps after it. A scenario without keys gets no numbering and one commit. A
/// failure between the first staged change and the commit discards the tracked changes, with or without keys.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.AnalyseScenarios;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.AnalyseScenarios;

[TestFixture]
public class AcceptAnalyseScenarioVariantTreesTests
{
    private const string NumberingFailure = "numbering failed";
    private const int SkippedWorks = 2;
    private static readonly DateOnly FromDate = new(2026, 5, 1);
    private static readonly DateOnly UntilDate = new(2026, 5, 31);

    private IAnalyseScenarioRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private IEscalationChainService _escalationChainService = null!;
    private AcceptAnalyseScenarioCommandHandler _handler = null!;
    private AnalyseScenario _scenario = null!;
    private IReadOnlyList<Guid> _rootIds = null!;
    private CancellationToken _ct;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IAnalyseScenarioRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _escalationChainService = Substitute.For<IEscalationChainService>();
        var complianceService = Substitute.For<IScenarioComplianceService>();
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
        _rootIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        _repository.Get(_scenario.Id).Returns(_scenario);
        _scenarioService.GetVariantTreeRootIdsAsync(_scenario.Token, _ct).Returns(_rootIds);
        _scenarioService.PromoteScenarioWorksAsync(_scenario.Token, FromDate, UntilDate, _ct)
            .Returns(new ScenarioAcceptLockSkips(SkippedWorks, 0, 0, 0, 0));
        complianceService
            .EvaluateAsync(FromDate, UntilDate, _scenario.GroupId, _scenario.Token, Arg.Any<CancellationToken>())
            .Returns(new ScenarioComplianceReport(new List<PeriodIssueDto>(), new List<PeriodIssueDto>()));

        _handler = new AcceptAnalyseScenarioCommandHandler(
            _repository,
            _scenarioService,
            _unitOfWork,
            Substitute.For<IWorkSofteningRepository>(),
            complianceService,
            Substitute.For<ISupervisorOverrideAuthorizer>(),
            Substitute.For<IScheduleTimelineService>(),
            Substitute.For<IAgentConditionRepository>(),
            Substitute.For<IAgentConditionLedgerService>(),
            Substitute.For<IHttpContextAccessor>(),
            _periodHoursService,
            Substitute.For<IWorkNotificationService>(),
            _escalationChainService,
            TestGroupWriteVisibility.UnrestrictedGroups(),
            Substitute.For<ILogger<AcceptAnalyseScenarioCommandHandler>>());
    }

    [Test]
    public async Task Accept_ReadsTheRootsBeforeThePromote_AndNumbersBetweenTheAcceptCommitAndThePeriodHours()
    {
        await _handler.Handle(new AcceptAnalyseScenarioCommand(_scenario.Id), _ct);

        Received.InOrder(() =>
        {
            _scenarioService.GetVariantTreeRootIdsAsync(_scenario.Token, _ct);
            _scenarioService.PromoteScenarioWorksAsync(_scenario.Token, FromDate, UntilDate, _ct);
            _unitOfWork.CompleteAsync();
            _scenarioService.RecalculateVariantTreesAsync(_rootIds, CancellationToken.None);
            _unitOfWork.CompleteAsync();
            _periodHoursService.RefreshCachedPeriodHoursAsync(FromDate, UntilDate, null);
        });
    }

    [Test]
    public async Task Accept_WithKeys_ReportsTheLockSkipsLikeAnyOtherAccept()
    {
        var result = await _handler.Handle(new AcceptAnalyseScenarioCommand(_scenario.Id), _ct);

        result.ShouldBe(new AcceptAnalyseScenarioResult(true, SkippedWorks));
    }

    [Test]
    public async Task Accept_NumberingFails_TheAcceptStands_TheValuesAreDropped_AndTheLaterStepsRun()
    {
        _scenarioService.RecalculateVariantTreesAsync(_rootIds, CancellationToken.None)
            .Returns(Task.FromException(new InvalidOperationException(NumberingFailure)));

        var result = await _handler.Handle(new AcceptAnalyseScenarioCommand(_scenario.Id), _ct);

        result.Accepted.ShouldBeTrue();
        _scenario.Status.ShouldBe(AnalyseScenarioStatus.Accepted);
        await _unitOfWork.Received(1).CompleteAsync();
        _unitOfWork.Received(1).DiscardTrackedChanges();
        await _periodHoursService.Received(1).RefreshCachedPeriodHoursAsync(FromDate, UntilDate, null);
        await _escalationChainService.Received(1).SupersedeAbsenceChainsForBreaksAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<string>(), _ct);
    }

    [Test]
    public async Task Accept_TheCommitOfTheNumberingFails_TheAcceptStands_AndTheValuesAreDropped()
    {
        _unitOfWork.CompleteAsync().Returns(
            _ => Task.CompletedTask,
            _ => Task.FromException(new InvalidOperationException(NumberingFailure)));

        var result = await _handler.Handle(new AcceptAnalyseScenarioCommand(_scenario.Id), _ct);

        result.Accepted.ShouldBeTrue();
        await _unitOfWork.Received(2).CompleteAsync();
        _unitOfWork.Received(1).DiscardTrackedChanges();
        await _periodHoursService.Received(1).RefreshCachedPeriodHoursAsync(FromDate, UntilDate, null);
    }

    [Test]
    public async Task Accept_TheAcceptCommitFails_NothingIsNumbered()
    {
        _unitOfWork.CompleteAsync().Returns(Task.FromException(new ConflictException(NumberingFailure)));

        await Should.ThrowAsync<ConflictException>(
            () => _handler.Handle(new AcceptAnalyseScenarioCommand(_scenario.Id), _ct));

        await _scenarioService.DidNotReceive().RecalculateVariantTreesAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Accept_WithKeys_CommitsThroughTheConditionalKeyCommit_ThenNumbersWithOnePlainCommit()
    {
        _scenarioService.TryCommitVariantAcceptAsync(_scenario.Token, _ct).Returns(true);

        await _handler.Handle(new AcceptAnalyseScenarioCommand(_scenario.Id), _ct);

        Received.InOrder(() =>
        {
            _scenarioService.PromoteScenarioWorksAsync(_scenario.Token, FromDate, UntilDate, _ct);
            _repository.Put(_scenario);
            _scenarioService.TryCommitVariantAcceptAsync(_scenario.Token, _ct);
            _scenarioService.RecalculateVariantTreesAsync(_rootIds, CancellationToken.None);
            _unitOfWork.CompleteAsync();
        });
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Accept_LosesTheRaceForAKey_IsRefusedAsConsumed_AndNothingRunsAfterIt()
    {
        _scenarioService.TryCommitVariantAcceptAsync(_scenario.Token, _ct)
            .Returns(Task.FromException<bool>(new ScenarioVariantConsumedException(["Winter keys"])));

        await Should.ThrowAsync<ScenarioVariantConsumedException>(
            () => _handler.Handle(new AcceptAnalyseScenarioCommand(_scenario.Id), _ct));

        await _unitOfWork.DidNotReceive().CompleteAsync();
        _unitOfWork.Received(1).DiscardTrackedChanges();
        await _scenarioService.DidNotReceive().RecalculateVariantTreesAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
        await _periodHoursService.DidNotReceive().RefreshCachedPeriodHoursAsync(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>());
    }

    [Test]
    public async Task Accept_FailsWhileStaging_DiscardsTheStagedChanges_AndCommitsNothing()
    {
        _scenarioService.PromoteScenarioWorksAsync(_scenario.Token, FromDate, UntilDate, _ct)
            .Returns(Task.FromException<ScenarioAcceptLockSkips>(new OperationCanceledException()));

        await Should.ThrowAsync<OperationCanceledException>(
            () => _handler.Handle(new AcceptAnalyseScenarioCommand(_scenario.Id), _ct));

        _unitOfWork.Received(1).DiscardTrackedChanges();
        await _unitOfWork.DidNotReceive().CompleteAsync();
        await _scenarioService.DidNotReceive().TryCommitVariantAcceptAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Accept_PlainCommitFails_DiscardsTheStagedChanges_AndRunsNothingAfterIt()
    {
        _unitOfWork.CompleteAsync().Returns(Task.FromException(new OperationCanceledException()));

        await Should.ThrowAsync<OperationCanceledException>(
            () => _handler.Handle(new AcceptAnalyseScenarioCommand(_scenario.Id), _ct));

        _unitOfWork.Received(1).DiscardTrackedChanges();
        await _scenarioService.DidNotReceive().RecalculateVariantTreesAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
        await _periodHoursService.DidNotReceive().RefreshCachedPeriodHoursAsync(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>());
    }

    [Test]
    public async Task Accept_WithoutKeys_NumbersNothing_AndCommitsOnce()
    {
        _scenarioService.GetVariantTreeRootIdsAsync(_scenario.Token, _ct).Returns(Array.Empty<Guid>());

        var result = await _handler.Handle(new AcceptAnalyseScenarioCommand(_scenario.Id), _ct);

        result.Accepted.ShouldBeTrue();
        await _unitOfWork.Received(1).CompleteAsync();
        await _scenarioService.DidNotReceive().RecalculateVariantTreesAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
        _unitOfWork.DidNotReceive().DiscardTrackedChanges();
    }
}
