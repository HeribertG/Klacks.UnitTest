// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Until 2026-10-01 the WorkChange endpoints ignored group visibility: any user could list every change of
/// the tenant, read one by id, and attach, edit or delete changes on shifts of employees outside their
/// groups, including moving a hidden employee in as replacement. A change whose parent Work or replacement
/// client is hidden must now be answered exactly like a missing one, and nothing may be written for it.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.WorkChanges;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Klacks.Api.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.WorkChanges;

[TestFixture]
public class WorkChangeVisibilityTests
{
    private static readonly DateOnly Day = new(2027, 5, 10);

    private IWorkChangeRepository _workChangeRepository = null!;
    private IWorkRepository _workRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private IScheduleCompletionService _completionService = null!;
    private IWorkChangeResultService _resultService = null!;
    private IDayLockService _dayLockService = null!;
    private Guid _visibleClientId;
    private Guid _hiddenClientId;

    [SetUp]
    public void SetUp()
    {
        _workChangeRepository = Substitute.For<IWorkChangeRepository>();
        _workRepository = Substitute.For<IWorkRepository>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _completionService = Substitute.For<IScheduleCompletionService>();
        _resultService = Substitute.For<IWorkChangeResultService>();
        _dayLockService = Substitute.For<IDayLockService>();

        _visibleClientId = Guid.NewGuid();
        _hiddenClientId = Guid.NewGuid();

        _clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Guid>(0) != _hiddenClientId);
        _clientVisibilityGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => !ci.ArgAt<IReadOnlyCollection<Guid>>(0).Contains(_hiddenClientId));
        _clientVisibilityGuard.FilterVisibleAsync(
                Arg.Any<IReadOnlyCollection<WorkChange>>(), Arg.Any<Func<WorkChange, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<WorkChange>>(0)
                .Where(wc => ci.ArgAt<Func<WorkChange, Guid>>(1)(wc) != _hiddenClientId)
                .ToList());
    }

    [Test]
    public async Task List_LeavesOutChangesOnWorksOfHiddenClients()
    {
        var visibleWork = NewWork(_visibleClientId);
        var hiddenWork = NewWork(_hiddenClientId);
        var visible = NewChange(visibleWork.Id);
        var hidden = NewChange(hiddenWork.Id);
        _workChangeRepository.List().Returns(new List<WorkChange> { visible, hidden });
        _workRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>()).Returns(new List<Work> { visibleWork, hiddenWork });
        var handler = new ListQueryHandler(
            _workChangeRepository, _workRepository, _clientVisibilityGuard, new ScheduleMapper(),
            Substitute.For<ILogger<ListQueryHandler>>());

        var result = (await handler.Handle(new ListQuery<WorkChangeResource>(), CancellationToken.None)).ToList();

        result.Select(r => r.Id).ShouldBe(new[] { visible.Id });
    }

    [Test]
    public async Task Get_ChangeOnAWorkOfAHiddenClient_IsAnsweredLikeAMissingChange()
    {
        var hiddenWork = NewWork(_hiddenClientId);
        var hidden = NewChange(hiddenWork.Id);
        hidden.Work = hiddenWork;
        var missingId = Guid.NewGuid();
        _workChangeRepository.Get(hidden.Id).Returns(hidden);
        _workChangeRepository.Get(missingId).Returns((WorkChange?)null);
        var handler = new GetQueryHandler(
            _workChangeRepository, _clientVisibilityGuard, new ScheduleMapper(), Substitute.For<ILogger<GetQueryHandler>>());

        var hiddenEx = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetQuery<WorkChangeResource>(hidden.Id), CancellationToken.None));
        var missingEx = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetQuery<WorkChangeResource>(missingId), CancellationToken.None));

        hiddenEx.Message.ShouldBe($"WorkChange with ID {hidden.Id} not found");
        missingEx.Message.ShouldBe($"WorkChange with ID {missingId} not found");
    }

    [Test]
    public async Task Post_OnAWorkOfAHiddenClient_IsRefusedLikeAMissingWork_NothingWritten()
    {
        var hiddenWork = NewWork(_hiddenClientId);
        var missingWorkId = Guid.NewGuid();
        _workRepository.GetNoTracking(hiddenWork.Id).Returns(hiddenWork);
        _workRepository.GetNoTracking(missingWorkId).Returns((Work?)null);
        var handler = NewPostHandler();

        var hiddenEx = await Should.ThrowAsync<InvalidRequestException>(() => handler.Handle(
            new PostCommand<WorkChangeResource>(NewResource(Guid.NewGuid(), hiddenWork.Id, null)), CancellationToken.None));
        var missingEx = await Should.ThrowAsync<InvalidRequestException>(() => handler.Handle(
            new PostCommand<WorkChangeResource>(NewResource(Guid.NewGuid(), missingWorkId, null)), CancellationToken.None));

        hiddenEx.Message.ShouldBe(missingEx.Message.Replace(missingWorkId.ToString(), hiddenWork.Id.ToString()));
        await _workChangeRepository.DidNotReceive().Add(Arg.Any<WorkChange>());
        await _dayLockService.DidNotReceiveWithAnyArgs().EnsureNotLockedAsync(default, default, default, default);
    }

    [Test]
    public async Task Post_ReplacingWithAHiddenClient_IsRefusedLikeAMissingWork_NothingWritten()
    {
        var visibleWork = NewWork(_visibleClientId);
        _workRepository.GetNoTracking(visibleWork.Id).Returns(visibleWork);

        await Should.ThrowAsync<InvalidRequestException>(() => NewPostHandler().Handle(
            new PostCommand<WorkChangeResource>(NewResource(Guid.NewGuid(), visibleWork.Id, _hiddenClientId)),
            CancellationToken.None));

        await _workChangeRepository.DidNotReceive().Add(Arg.Any<WorkChange>());
        await _dayLockService.DidNotReceiveWithAnyArgs().EnsureNotLockedAsync(default, default, default, default);
    }

    [Test]
    public async Task Put_OfAChangeOnAWorkOfAHiddenClient_IsAnsweredLikeAMissingChange_NothingWritten()
    {
        var hiddenWork = NewWork(_hiddenClientId);
        var stored = NewChange(hiddenWork.Id);
        _workChangeRepository.GetNoTracking(stored.Id).Returns(stored);
        _workRepository.GetNoTracking(hiddenWork.Id).Returns(hiddenWork);

        var result = await NewPutHandler().Handle(
            new PutCommand<WorkChangeResource>(NewResource(stored.Id, hiddenWork.Id, null)), CancellationToken.None);

        result.ShouldBeNull();
        await _workChangeRepository.DidNotReceive().Put(Arg.Any<WorkChange>());
        await _dayLockService.DidNotReceiveWithAnyArgs().EnsureNotLockedAsync(default, default, default, default);
    }

    [Test]
    public async Task Put_ReplacingWithAHiddenClient_IsAnsweredLikeAMissingChange_NothingWritten()
    {
        var visibleWork = NewWork(_visibleClientId);
        var stored = NewChange(visibleWork.Id);
        _workChangeRepository.GetNoTracking(stored.Id).Returns(stored);
        _workRepository.GetNoTracking(visibleWork.Id).Returns(visibleWork);

        var result = await NewPutHandler().Handle(
            new PutCommand<WorkChangeResource>(NewResource(stored.Id, visibleWork.Id, _hiddenClientId)),
            CancellationToken.None);

        result.ShouldBeNull();
        await _workChangeRepository.DidNotReceive().Put(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Put_MovingAChangeAwayFromAWorkOfAHiddenClient_IsAnsweredLikeAMissingChange_NothingWritten()
    {
        var hiddenWork = NewWork(_hiddenClientId);
        var visibleWork = NewWork(_visibleClientId);
        var stored = NewChange(hiddenWork.Id);
        _workChangeRepository.GetNoTracking(stored.Id).Returns(stored);
        _workRepository.GetNoTracking(hiddenWork.Id).Returns(hiddenWork);
        _workRepository.GetNoTracking(visibleWork.Id).Returns(visibleWork);

        var result = await NewPutHandler().Handle(
            new PutCommand<WorkChangeResource>(NewResource(stored.Id, visibleWork.Id, null)), CancellationToken.None);

        result.ShouldBeNull();
        await _workChangeRepository.DidNotReceive().Put(Arg.Any<WorkChange>());
        await _clientVisibilityGuard.Received(1)
            .AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_ChangeOnAWorkOfAHiddenClient_IsAnsweredLikeAMissingChange_NothingDeleted()
    {
        var hiddenWork = NewWork(_hiddenClientId);
        var stored = NewChange(hiddenWork.Id);
        _workChangeRepository.Get(stored.Id).Returns(stored);
        _workRepository.GetNoTracking(hiddenWork.Id).Returns(hiddenWork);
        var handler = new DeleteCommandHandler(
            _workChangeRepository, _workRepository, _clientVisibilityGuard, new ScheduleMapper(), _periodHoursService,
            Substitute.For<IWorkNotificationService>(), _completionService, _resultService,
            Substitute.For<IHttpContextAccessor>(), _dayLockService, Substitute.For<ILogger<DeleteCommandHandler>>());

        var result = await handler.Handle(new DeleteCommand<WorkChangeResource>(stored.Id), CancellationToken.None);

        result.ShouldBeNull();
        await _workChangeRepository.DidNotReceive().Delete(Arg.Any<Guid>());
        await _dayLockService.DidNotReceiveWithAnyArgs().EnsureNotLockedAsync(default, default, default, default);
    }

    private PostCommandHandler NewPostHandler()
        => new(
            _workChangeRepository, _workRepository, _clientVisibilityGuard, new ScheduleMapper(), _periodHoursService,
            Substitute.For<IWorkNotificationService>(), _completionService, _resultService,
            Substitute.For<IHttpContextAccessor>(), _dayLockService, Substitute.For<IPreCommitConflictChecker>(),
            Substitute.For<ISupervisorOverrideAuthorizer>(), Substitute.For<ILogger<PostCommandHandler>>());

    private PutCommandHandler NewPutHandler()
        => new(
            _workChangeRepository, _workRepository, _clientVisibilityGuard, new ScheduleMapper(), _periodHoursService,
            _completionService, _resultService, Substitute.For<IWorkNotificationFacade>(), _dayLockService,
            Substitute.For<ILogger<PutCommandHandler>>());

    private static Work NewWork(Guid clientId)
        => new() { Id = Guid.NewGuid(), ClientId = clientId, ShiftId = Guid.NewGuid(), CurrentDate = Day };

    private static WorkChange NewChange(Guid workId)
        => new() { Id = Guid.NewGuid(), WorkId = workId };

    private static WorkChangeResource NewResource(Guid id, Guid workId, Guid? replaceClientId)
        => new() { Id = id, WorkId = workId, ReplaceClientId = replaceClientId };
}
