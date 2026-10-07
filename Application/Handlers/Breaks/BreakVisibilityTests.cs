// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Until 2026-10-01 every Breaks endpoint (list, get, create, update, delete, bulk add, bulk delete,
/// unconfirm) ignored group visibility, so a group-restricted user could read the absence type (health
/// data) of any employee and write absences for employees outside their groups. A break owned by a hidden
/// client must now be answered exactly like a missing one, and nothing may be written for it.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Breaks;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Breaks;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Klacks.Api.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Breaks;

[TestFixture]
public class BreakVisibilityTests
{
    private static readonly DateOnly Day = new(2026, 3, 9);

    private IBreakRepository _breakRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IBreakMacroService _breakMacroService = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private IScheduleEntriesService _scheduleEntriesService = null!;
    private IWorkNotificationService _notificationService = null!;
    private IScheduleCompletionService _completionService = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private ISelectedGroupContextResolver _groupContextResolver = null!;
    private IDayLockService _dayLockService = null!;
    private Guid _visibleClientId;
    private Guid _hiddenClientId;

    [SetUp]
    public void SetUp()
    {
        _breakRepository = Substitute.For<IBreakRepository>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _breakMacroService = Substitute.For<IBreakMacroService>();
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _scheduleEntriesService = Substitute.For<IScheduleEntriesService>();
        _notificationService = Substitute.For<IWorkNotificationService>();
        _completionService = Substitute.For<IScheduleCompletionService>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _groupContextResolver = Substitute.For<ISelectedGroupContextResolver>();
        _dayLockService = Substitute.For<IDayLockService>();

        _visibleClientId = Guid.NewGuid();
        _hiddenClientId = Guid.NewGuid();

        _clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Guid>(0) != _hiddenClientId);
        _clientVisibilityGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => !ci.ArgAt<IReadOnlyCollection<Guid>>(0).Contains(_hiddenClientId));
        _clientVisibilityGuard.FilterVisibleAsync(
                Arg.Any<IReadOnlyCollection<Break>>(), Arg.Any<Func<Break, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<Break>>(0)
                .Where(b => ci.ArgAt<Func<Break, Guid>>(1)(b) != _hiddenClientId)
                .ToList());
    }

    [Test]
    public async Task List_LeavesOutBreaksOfHiddenClients()
    {
        var visible = NewBreak(_visibleClientId);
        var hidden = NewBreak(_hiddenClientId);
        _breakRepository.List().Returns(new List<Break> { visible, hidden });
        var handler = new ListQueryHandler(
            _breakRepository, _clientVisibilityGuard, new ScheduleMapper(), Substitute.For<ILogger<ListQueryHandler>>());

        var result = (await handler.Handle(new ListQuery<BreakResource>(), CancellationToken.None)).ToList();

        result.Select(r => r.Id).ShouldBe(new[] { visible.Id });
    }

    [Test]
    public async Task Get_BreakOfHiddenClient_IsAnsweredLikeAMissingBreak()
    {
        var hidden = NewBreak(_hiddenClientId);
        var missingId = Guid.NewGuid();
        _breakRepository.Get(hidden.Id).Returns(hidden);
        _breakRepository.Get(missingId).Returns((Break?)null);
        var handler = new GetQueryHandler(
            _breakRepository, _clientVisibilityGuard, new ScheduleMapper(), Substitute.For<ILogger<GetQueryHandler>>());

        var hiddenEx = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetQuery<BreakResource>(hidden.Id), CancellationToken.None));
        var missingEx = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetQuery<BreakResource>(missingId), CancellationToken.None));

        hiddenEx.Message.ShouldBe($"Break with ID {hidden.Id} not found");
        missingEx.Message.ShouldBe($"Break with ID {missingId} not found");
    }

    [Test]
    public async Task Post_ForHiddenClient_IsRefusedLikeAMissingClient_NothingWritten()
    {
        var handler = new PostCommandHandler(
            _breakRepository, _clientVisibilityGuard, _breakMacroService, new ScheduleMapper(), _periodHoursService,
            _scheduleEntriesService, _notificationService, _completionService, _httpContextAccessor,
            _groupContextResolver, _dayLockService, Substitute.For<ILogger<PostCommandHandler>>());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(
            new PostCommand<BreakResource>(NewResource(Guid.NewGuid(), _hiddenClientId)), CancellationToken.None));

        ex.Message.ShouldBe($"Client with ID {_hiddenClientId} not found");
        await _breakRepository.DidNotReceive().Add(Arg.Any<Break>());
        await _dayLockService.DidNotReceiveWithAnyArgs().EnsureNotLockedAsync(default, default, default, default);
    }

    [Test]
    public async Task Put_OfABreakOwnedByAHiddenClient_IsRefusedLikeAMissingBreak_NothingWritten()
    {
        var stored = NewBreak(_hiddenClientId);
        _breakRepository.GetNoTracking(stored.Id).Returns(stored);
        var handler = NewPutHandler();

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(
            new PutCommand<BreakResource>(NewResource(stored.Id, _visibleClientId)), CancellationToken.None));

        ex.Message.ShouldBe($"Break with ID {stored.Id} not found");
        await _breakRepository.DidNotReceive().Put(Arg.Any<Break>());
    }

    [Test]
    public async Task Put_MovingAVisibleBreakOntoAHiddenClient_IsRefusedLikeAMissingBreak_NothingWritten()
    {
        var stored = NewBreak(_visibleClientId);
        _breakRepository.GetNoTracking(stored.Id).Returns(stored);
        var handler = NewPutHandler();

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(
            new PutCommand<BreakResource>(NewResource(stored.Id, _hiddenClientId)), CancellationToken.None));

        ex.Message.ShouldBe($"Break with ID {stored.Id} not found");
        await _breakRepository.DidNotReceive().Put(Arg.Any<Break>());
    }

    [Test]
    public async Task Delete_BreakOfHiddenClient_IsRefusedLikeAMissingBreak_NothingDeleted()
    {
        var hidden = NewBreak(_hiddenClientId);
        _breakRepository.Get(hidden.Id).Returns(hidden);
        var handler = new DeleteCommandHandler(
            _breakRepository, _clientVisibilityGuard, new ScheduleMapper(), _periodHoursService,
            _scheduleEntriesService, _notificationService, _completionService, _httpContextAccessor,
            _groupContextResolver, _dayLockService, Substitute.For<ILogger<DeleteCommandHandler>>());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(
            new DeleteBreakCommand(hidden.Id, Day, Day), CancellationToken.None));

        ex.Message.ShouldBe($"Break with ID {hidden.Id} not found.");
        await _breakRepository.DidNotReceive().Delete(Arg.Any<Guid>());
    }

    [Test]
    public async Task BulkAdd_WithOneHiddenClient_IsRefusedAsAWhole_NothingWritten()
    {
        var handler = new BulkAddBreaksCommandHandler(
            _breakRepository, _clientVisibilityGuard, _breakMacroService, _periodHoursService, _completionService,
            _dayLockService, Substitute.For<ILogger<BulkAddBreaksCommandHandler>>());
        var request = new BulkAddBreaksRequest
        {
            PeriodStart = Day,
            PeriodEnd = Day,
            Breaks =
            [
                new BulkBreakItem { ClientId = _visibleClientId, AbsenceId = Guid.NewGuid(), CurrentDate = Day },
                new BulkBreakItem { ClientId = _hiddenClientId, AbsenceId = Guid.NewGuid(), CurrentDate = Day }
            ]
        };

        await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new BulkAddBreaksCommand(request), CancellationToken.None));

        await _breakRepository.DidNotReceive().Add(Arg.Any<Break>());
        await _completionService.DidNotReceiveWithAnyArgs().SaveBulkAndTrackAsync(default!);
    }

    [Test]
    public async Task BulkDelete_BreaksOfHiddenClients_AreTreatedLikeMissingIds_NotRemoved()
    {
        var visible = NewBreak(_visibleClientId);
        var hidden = NewBreak(_hiddenClientId);
        var missingId = Guid.NewGuid();
        _breakRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>()).Returns(new List<Break> { visible, hidden });
        _periodHoursService.GetPeriodBoundariesAsync(Arg.Any<DateOnly>()).Returns((Day, Day));
        var handler = new BulkDeleteBreaksCommandHandler(
            _breakRepository, _clientVisibilityGuard, _periodHoursService, _completionService, _dayLockService,
            Substitute.For<ILogger<BulkDeleteBreaksCommandHandler>>());

        var response = await handler.Handle(
            new BulkDeleteBreaksCommand(new BulkDeleteBreaksRequest
            {
                BreakIds = [visible.Id, hidden.Id, missingId],
                PeriodStart = Day,
                PeriodEnd = Day
            }),
            CancellationToken.None);

        _breakRepository.Received(1).Remove(visible);
        _breakRepository.DidNotReceive().Remove(hidden);
        response.DeletedIds.ShouldBe(new[] { visible.Id });
        response.FailedCount.ShouldBe(2);
    }

    [Test]
    public async Task Unconfirm_BreakOfHiddenClient_IsRefusedLikeAMissingBreak_NothingWritten()
    {
        var hidden = NewBreak(_hiddenClientId);
        _breakRepository.Get(hidden.Id).Returns(hidden);
        var lockLevelService = Substitute.For<IWorkLockLevelService>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var handler = new UnconfirmBreakCommandHandler(
            _breakRepository, _clientVisibilityGuard, unitOfWork, lockLevelService, new ScheduleMapper(),
            Substitute.For<IBreakUserContextProvider>(), Substitute.For<ILogger<UnconfirmBreakCommandHandler>>());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new UnconfirmBreakCommand(hidden.Id), CancellationToken.None));

        ex.Message.ShouldBe($"Break with ID {hidden.Id} not found.");
        lockLevelService.DidNotReceiveWithAnyArgs().Unseal(default!, default, default);
        await _breakRepository.DidNotReceive().Put(Arg.Any<Break>());
        await unitOfWork.DidNotReceive().CompleteAsync();
    }

    private PutCommandHandler NewPutHandler()
        => new(
            _breakRepository, _clientVisibilityGuard, _breakMacroService, new ScheduleMapper(), _periodHoursService,
            _scheduleEntriesService, _notificationService, _completionService, _httpContextAccessor,
            _groupContextResolver, _dayLockService, Substitute.For<ILogger<PutCommandHandler>>());

    [Test]
    public async Task Put_OfAClosedBreak_ByNonAdmin_IsRefused_NothingWritten()
    {
        var stored = NewBreak(_visibleClientId);
        stored.LockLevel = WorkLockLevel.Closed;
        _breakRepository.GetNoTracking(stored.Id).Returns(stored);
        Klacks.UnitTest.Application.Handlers.PeriodClosing.PeriodClosingTestHelpers.GivenUserIsNotAdmin(_httpContextAccessor);
        var handler = NewPutHandler();

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => handler.Handle(
            new PutCommand<BreakResource>(NewResource(stored.Id, _visibleClientId)), CancellationToken.None));

        ex.Message.ShouldContain("closed");
        await _breakRepository.DidNotReceive().Put(Arg.Any<Break>());
    }

    [Test]
    public async Task BulkDelete_OnASealedDay_IsRefused_NothingRemoved()
    {
        var visible = NewBreak(_visibleClientId);
        _breakRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>()).Returns(new List<Break> { visible });
        _dayLockService.EnsureNoneLockedAsync(
                Arg.Any<IReadOnlyCollection<(DateOnly Date, Guid ClientId, Guid? AnalyseToken)>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidRequestException("Day is sealed and cannot be modified.")));
        var handler = new BulkDeleteBreaksCommandHandler(
            _breakRepository, _clientVisibilityGuard, _periodHoursService, _completionService, _dayLockService,
            Substitute.For<ILogger<BulkDeleteBreaksCommandHandler>>());

        await Should.ThrowAsync<InvalidRequestException>(() => handler.Handle(
            new BulkDeleteBreaksCommand(new BulkDeleteBreaksRequest
            {
                BreakIds = [visible.Id],
                PeriodStart = Day,
                PeriodEnd = Day
            }),
            CancellationToken.None));

        _breakRepository.DidNotReceive().Remove(Arg.Any<Break>());
    }
    private static Break NewBreak(Guid clientId)
        => new() { Id = Guid.NewGuid(), ClientId = clientId, AbsenceId = Guid.NewGuid(), CurrentDate = Day };

    private static BreakResource NewResource(Guid id, Guid clientId)
        => new() { Id = id, ClientId = clientId, AbsenceId = Guid.NewGuid(), CurrentDate = Day };
}
