// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization of the WorkChanges PutCommandHandler success and early-return paths (round 12 split of Handle):
/// which client results and schedule notifications a stored, changed or removed replacement produces and in which order,
/// what reaches the completion service, the day-lock order on a move, and the null and mapped-only answers.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.WorkChanges;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.UnitTest.Application.Handlers.Works;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Handlers.WorkChanges;

[TestFixture]
public class WorkChangePutCommandHandlerCharacterizationTests
{
    private const string UserName = "test-user";
    private const string ConnectionId = "conn-1";
    private static readonly DateOnly Day = new(2027, 5, 10);
    private static readonly DateOnly PeriodStart = new(2027, 5, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 5, 31);

    private IWorkChangeRepository _workChangeRepository = null!;
    private IWorkRepository _workRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IDayLockService _dayLockService = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private IScheduleCompletionService _completionService = null!;
    private IWorkChangeResultService _resultService = null!;
    private IWorkNotificationFacade _notificationFacade = null!;
    private IReplacementRequestRecorder _replacementRequestRecorder = null!;

    [SetUp]
    public void SetUp()
    {
        _workChangeRepository = Substitute.For<IWorkChangeRepository>();
        _workRepository = Substitute.For<IWorkRepository>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _dayLockService = Substitute.For<IDayLockService>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _completionService = Substitute.For<IScheduleCompletionService>();
        _resultService = Substitute.For<IWorkChangeResultService>();
        _notificationFacade = Substitute.For<IWorkNotificationFacade>();
        _replacementRequestRecorder = Substitute.For<IReplacementRequestRecorder>();

        _clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _clientVisibilityGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _workChangeRepository.Put(Arg.Any<WorkChange>()).Returns(ci => ci.ArgAt<WorkChange>(0));
        _periodHoursService.GetPeriodBoundariesAsync(Day).Returns((PeriodStart, PeriodEnd));
        _resultService.GetClientResultAsync(
                Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(ci => new WorkChangeClientResult { ClientId = ci.ArgAt<Guid>(0) });
        _notificationFacade.GetConnectionId().Returns(ConnectionId);

        WorksTestHelpers.GivenUserIsAdmin(_httpContextAccessor, UserName);
    }

    [Test]
    public async Task Put_OfAnUnknownChange_ReturnsNull_AndReadsNoParentWork()
    {
        var result = await NewHandler().Handle(
            new PutCommand<WorkChangeResource>(NewResource(Guid.NewGuid(), Guid.NewGuid())), CancellationToken.None);

        result.ShouldBeNull();
        await _workRepository.DidNotReceiveWithAnyArgs().GetNoTracking(default);
        await _workChangeRepository.DidNotReceiveWithAnyArgs().Put(default!);
    }

    [Test]
    public async Task Put_WhenTheRepositoryAnswersNull_ReturnsNull_AndCompletesNothing()
    {
        var work = NewWork(null);
        var stored = NewChange(work.Id);
        Given(stored, work);
        _workChangeRepository.Put(Arg.Any<WorkChange>()).Returns((WorkChange?)null);

        var result = await NewHandler().Handle(
            new PutCommand<WorkChangeResource>(NewResource(stored.Id, work.Id)), CancellationToken.None);

        result.ShouldBeNull();
        await _workRepository.DidNotReceiveWithAnyArgs().Get(default);
        await _completionService.DidNotReceiveWithAnyArgs()
            .SaveAndTrackWithReplaceClientAsync(default, default, default, default, default, default, default);
        _notificationFacade.DidNotReceive().GetConnectionId();
    }

    [Test]
    public async Task Put_WhenTheWorkIsGoneAfterTheWrite_ReturnsTheMappedChange_WithoutPeriodOrResults()
    {
        var work = NewWork(null);
        var stored = NewChange(work.Id);
        Given(stored, work);
        _workRepository.Get(work.Id).Returns((Work?)null);

        var result = await NewHandler().Handle(
            new PutCommand<WorkChangeResource>(NewResource(stored.Id, work.Id)), CancellationToken.None);

        result.ShouldNotBeNull();
        result.Id.ShouldBe(stored.Id);
        result.WorkId.ShouldBe(work.Id);
        result.PeriodStart.ShouldBeNull();
        result.PeriodEnd.ShouldBeNull();
        result.ClientResults.ShouldBeEmpty();
        await _workRepository.Received(1).Get(work.Id);
        await _replacementRequestRecorder.DidNotReceiveWithAnyArgs().SyncManualReplacementAsync(default!, default!, default);
        await _completionService.DidNotReceiveWithAnyArgs()
            .SaveAndTrackWithReplaceClientAsync(default, default, default, default, default, default, default);
        _notificationFacade.DidNotReceive().GetConnectionId();
    }

    [Test]
    public async Task Put_SwitchingTheReplacement_ReportsOwnerNewAndPreviousReplacement_InThatOrder()
    {
        var token = Guid.NewGuid();
        var work = NewWork(token);
        var previousReplacement = Guid.NewGuid();
        var newReplacement = Guid.NewGuid();
        var stored = NewChange(work.Id, previousReplacement);
        Given(stored, work);
        var resource = NewResource(stored.Id, work.Id, newReplacement);

        var result = await NewHandler().Handle(new PutCommand<WorkChangeResource>(resource), CancellationToken.None);

        result.ShouldNotBeNull();
        result.PeriodStart.ShouldBe(PeriodStart);
        result.PeriodEnd.ShouldBe(PeriodEnd);
        result.ClientResults.Select(r => r.ClientId).ShouldBe([work.ClientId, newReplacement, previousReplacement]);
        await _completionService.Received(1).SaveAndTrackWithReplaceClientAsync(
            work.ClientId, Day, PeriodStart, PeriodEnd, newReplacement, token, previousReplacement);
        await _resultService.Received(1).GetClientResultAsync(
            work.ClientId, PeriodStart, PeriodEnd, Day, Arg.Any<CancellationToken>(), token);

        Received.InOrder(() =>
        {
            _workChangeRepository.Put(Arg.Any<WorkChange>());
            _workRepository.Get(work.Id);
            _replacementRequestRecorder.SyncManualReplacementAsync(work, Arg.Any<WorkChange>(), Arg.Any<CancellationToken>());
            _periodHoursService.GetPeriodBoundariesAsync(Day);
            _completionService.SaveAndTrackWithReplaceClientAsync(
                work.ClientId, Day, PeriodStart, PeriodEnd, newReplacement, token, previousReplacement);
            _resultService.GetClientResultAsync(
                work.ClientId, PeriodStart, PeriodEnd, Day, Arg.Any<CancellationToken>(), token);
            _resultService.GetClientResultAsync(
                newReplacement, PeriodStart, PeriodEnd, Day, Arg.Any<CancellationToken>(), token);
            _resultService.GetClientResultAsync(
                previousReplacement, PeriodStart, PeriodEnd, Day, Arg.Any<CancellationToken>(), token);
            _notificationFacade.GetConnectionId();
            _notificationFacade.NotifyScheduleUpdatedAsync(work.ClientId, Day, ConnectionId, PeriodStart, PeriodEnd, token);
            _notificationFacade.NotifyScheduleUpdatedAsync(newReplacement, Day, ConnectionId, PeriodStart, PeriodEnd, token);
            _notificationFacade.NotifyScheduleUpdatedAsync(previousReplacement, Day, ConnectionId, PeriodStart, PeriodEnd, token);
        });
    }

    [Test]
    public async Task Put_KeepingTheReplacement_PassesNoPreviousReplacement_AndReportsItOnce()
    {
        var work = NewWork(null);
        var replacement = Guid.NewGuid();
        var stored = NewChange(work.Id, replacement);
        Given(stored, work);

        var result = await NewHandler().Handle(
            new PutCommand<WorkChangeResource>(NewResource(stored.Id, work.Id, replacement)), CancellationToken.None);

        result.ShouldNotBeNull();
        result.ClientResults.Select(r => r.ClientId).ShouldBe([work.ClientId, replacement]);
        await _completionService.Received(1).SaveAndTrackWithReplaceClientAsync(
            work.ClientId, Day, PeriodStart, PeriodEnd, replacement, null, null);
        await _notificationFacade.Received(2).NotifyScheduleUpdatedAsync(
            Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>());
        await _notificationFacade.Received(1).NotifyScheduleUpdatedAsync(
            replacement, Day, ConnectionId, PeriodStart, PeriodEnd, null);
    }

    [Test]
    public async Task Put_RemovingTheReplacement_ReportsOwnerAndPreviousReplacement()
    {
        var work = NewWork(null);
        var previousReplacement = Guid.NewGuid();
        var stored = NewChange(work.Id, previousReplacement);
        Given(stored, work);

        var result = await NewHandler().Handle(
            new PutCommand<WorkChangeResource>(NewResource(stored.Id, work.Id)), CancellationToken.None);

        result.ShouldNotBeNull();
        result.ClientResults.Select(r => r.ClientId).ShouldBe([work.ClientId, previousReplacement]);
        await _completionService.Received(1).SaveAndTrackWithReplaceClientAsync(
            work.ClientId, Day, PeriodStart, PeriodEnd, null, null, previousReplacement);
        Received.InOrder(() =>
        {
            _notificationFacade.NotifyScheduleUpdatedAsync(work.ClientId, Day, ConnectionId, PeriodStart, PeriodEnd, null);
            _notificationFacade.NotifyScheduleUpdatedAsync(previousReplacement, Day, ConnectionId, PeriodStart, PeriodEnd, null);
        });
    }

    [Test]
    public async Task Put_WithoutAnyReplacement_ReportsOnlyTheOwner()
    {
        var work = NewWork(null);
        var stored = NewChange(work.Id);
        Given(stored, work);

        var result = await NewHandler().Handle(
            new PutCommand<WorkChangeResource>(NewResource(stored.Id, work.Id)), CancellationToken.None);

        result.ShouldNotBeNull();
        result.ClientResults.Select(r => r.ClientId).ShouldBe([work.ClientId]);
        await _completionService.Received(1).SaveAndTrackWithReplaceClientAsync(
            work.ClientId, Day, PeriodStart, PeriodEnd, null, null, null);
        await _notificationFacade.Received(1).NotifyScheduleUpdatedAsync(
            Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>());
    }

    [Test]
    public async Task Put_MovingTheChange_ChecksNewThenOldDay_ThenNewThenOldChange_BeforeTheWrite()
    {
        var oldWork = NewWork(null);
        var newWork = NewWork(null);
        var stored = NewChange(oldWork.Id);
        Given(stored, oldWork, newWork);

        await NewHandler().Handle(
            new PutCommand<WorkChangeResource>(NewResource(stored.Id, newWork.Id)), CancellationToken.None);

        Received.InOrder(() =>
        {
            _workChangeRepository.GetNoTracking(stored.Id);
            _workRepository.GetNoTracking(newWork.Id);
            _workRepository.GetNoTracking(oldWork.Id);
            _dayLockService.EnsureNotLockedAsync(newWork.CurrentDate, newWork.ClientId, null, Arg.Any<CancellationToken>());
            _dayLockService.EnsureNotLockedAsync(oldWork.CurrentDate, oldWork.ClientId, null, Arg.Any<CancellationToken>());
            _dayLockService.EnsureWorkChangeNotLockedAsync(newWork, Arg.Any<WorkChange>(), Arg.Any<CancellationToken>());
            _dayLockService.EnsureWorkChangeNotLockedAsync(oldWork, stored, Arg.Any<CancellationToken>());
            _workChangeRepository.Put(Arg.Any<WorkChange>());
        });
    }

    [Test]
    public async Task Put_KeepingTheParent_ChecksTheDayOnce_AndBothChangeStates()
    {
        var work = NewWork(null);
        var stored = NewChange(work.Id);
        Given(stored, work);

        await NewHandler().Handle(
            new PutCommand<WorkChangeResource>(NewResource(stored.Id, work.Id)), CancellationToken.None);

        await _workRepository.Received(1).GetNoTracking(work.Id);
        await _dayLockService.Received(1).EnsureNotLockedAsync(
            Arg.Any<DateOnly>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await _dayLockService.Received(2).EnsureWorkChangeNotLockedAsync(
            work, Arg.Any<WorkChange>(), Arg.Any<CancellationToken>());
    }

    private void Given(WorkChange stored, params Work[] works)
    {
        _workChangeRepository.GetNoTracking(stored.Id).Returns(stored);
        foreach (var work in works)
        {
            _workRepository.GetNoTracking(work.Id).Returns(work);
            _workRepository.Get(work.Id).Returns(work);
        }
    }

    private PutCommandHandler NewHandler()
        => new(
            _workChangeRepository, _workRepository, TestScheduleRowAccess.Over(_clientVisibilityGuard), new ScheduleMapper(),
            _periodHoursService, _completionService, _resultService, _notificationFacade, _dayLockService,
            _replacementRequestRecorder, _httpContextAccessor, new ParentWorkLockGuard(new WorkLockLevelService()),
            Substitute.For<ILogger<PutCommandHandler>>());

    private static Work NewWork(Guid? analyseToken)
        => new()
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            ShiftId = Guid.NewGuid(),
            CurrentDate = Day,
            AnalyseToken = analyseToken
        };

    private static WorkChange NewChange(Guid workId, Guid? replaceClientId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            WorkId = workId,
            Type = replaceClientId.HasValue ? WorkChangeType.ReplacementWithin : WorkChangeType.CorrectionStart,
            ReplaceClientId = replaceClientId
        };

    private static WorkChangeResource NewResource(Guid id, Guid workId, Guid? replaceClientId = null)
        => new()
        {
            Id = id,
            WorkId = workId,
            Type = replaceClientId.HasValue ? WorkChangeType.ReplacementWithin : WorkChangeType.CorrectionStart,
            ReplaceClientId = replaceClientId
        };
}
