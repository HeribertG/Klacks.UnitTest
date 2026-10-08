// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Until 2026-10-01 the Works endpoints (get, create, update, delete, bulk add, bulk delete, reassign,
/// confirm, unconfirm, restore, container children, day approval) ignored group visibility, so a
/// group-restricted user could read and write shifts of employees outside their groups by id. A Work owned
/// by a hidden client must now be answered exactly like a missing one, and nothing may be written for it.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Queries.Works;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class WorkVisibilityTests
{
    private static readonly DateOnly Day = new(2027, 4, 12);

    private IWorkRepository _workRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private IScheduleEntriesService _scheduleEntriesService = null!;
    private IScheduleCompletionService _completionService = null!;
    private IWorkNotificationFacade _notificationFacade = null!;
    private IContainerWorkCascadeService _cascadeService = null!;
    private ISelectedGroupContextResolver _groupContextResolver = null!;
    private IDayLockService _dayLockService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IOvertimeCascadeService _overtimeCascadeService = null!;
    private IPreCommitConflictChecker _conflictChecker = null!;
    private IWorkWriteGuard _writeGuard = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private Guid _visibleClientId;
    private Guid _hiddenClientId;

    [SetUp]
    public void SetUp()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _scheduleEntriesService = Substitute.For<IScheduleEntriesService>();
        _completionService = Substitute.For<IScheduleCompletionService>();
        _notificationFacade = Substitute.For<IWorkNotificationFacade>();
        _cascadeService = Substitute.For<IContainerWorkCascadeService>();
        _groupContextResolver = Substitute.For<ISelectedGroupContextResolver>();
        _dayLockService = Substitute.For<IDayLockService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _overtimeCascadeService = Substitute.For<IOvertimeCascadeService>();
        _conflictChecker = Substitute.For<IPreCommitConflictChecker>();
        _writeGuard = Substitute.For<IWorkWriteGuard>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();

        _visibleClientId = Guid.NewGuid();
        _hiddenClientId = Guid.NewGuid();

        _clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Guid>(0) != _hiddenClientId);
        _clientVisibilityGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => !ci.ArgAt<IReadOnlyCollection<Guid>>(0).Contains(_hiddenClientId));
        _clientVisibilityGuard.FilterVisibleAsync(
                Arg.Any<IReadOnlyCollection<Work>>(), Arg.Any<Func<Work, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<Work>>(0)
                .Where(w => ci.ArgAt<Func<Work, Guid>>(1)(w) != _hiddenClientId)
                .ToList());
    }

    [Test]
    public async Task Get_WorkOfHiddenClient_IsAnsweredLikeAMissingWork()
    {
        var hidden = NewWork(_hiddenClientId);
        var missingId = Guid.NewGuid();
        _workRepository.Get(hidden.Id).Returns(hidden);
        _workRepository.Get(missingId).Returns((Work?)null);
        var handler = new GetQueryHandler(
            _workRepository, _clientVisibilityGuard, new ScheduleMapper(), Substitute.For<ILogger<GetQueryHandler>>());

        var hiddenEx = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetQuery<WorkResource>(hidden.Id), CancellationToken.None));
        var missingEx = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetQuery<WorkResource>(missingId), CancellationToken.None));

        hiddenEx.Message.ShouldBe($"Work with ID {hidden.Id} not found");
        missingEx.Message.ShouldBe($"Work with ID {missingId} not found");
    }

    [Test]
    public async Task Post_ForHiddenClient_IsRefusedLikeAMissingClient_NothingWritten()
    {
        var handler = new PostCommandHandler(
            _workRepository, _clientVisibilityGuard, new ScheduleMapper(), _periodHoursService,
            _scheduleEntriesService, _completionService, _notificationFacade,
            Substitute.For<IShiftDefaultExpensesApplier>(),
            Substitute.For<IContainerWorkExpansionService>(), _groupContextResolver, _dayLockService, _unitOfWork,
            _overtimeCascadeService, _writeGuard, Substitute.For<ILogger<PostCommandHandler>>());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(
            new PostCommand<WorkResource>(NewResource(Guid.NewGuid(), _hiddenClientId)), CancellationToken.None));

        ex.Message.ShouldBe($"Client with ID {_hiddenClientId} not found");
        await _workRepository.DidNotReceive().Add(Arg.Any<Work>());
        await _dayLockService.DidNotReceiveWithAnyArgs().EnsureNotLockedAsync(default, default, default, default);
        await _writeGuard.DidNotReceiveWithAnyArgs().EnsureNoSporadicConflictAsync(default!, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Put_OfAWorkOwnedByAHiddenClient_IsAnsweredLikeAMissingWork_NothingWritten()
    {
        var stored = NewWork(_hiddenClientId);
        _workRepository.GetNoTracking(stored.Id).Returns(stored);

        var result = await NewPutHandler().Handle(
            new PutCommand<WorkResource>(NewResource(stored.Id, _visibleClientId)), CancellationToken.None);

        result.ShouldBeNull();
        await _workRepository.DidNotReceive().Put(Arg.Any<Work>());
        await _dayLockService.DidNotReceiveWithAnyArgs().EnsureNotLockedAsync(default, default, default, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Put_MovingAVisibleWorkOntoAHiddenClient_IsAnsweredLikeAMissingWork_NothingWritten()
    {
        var stored = NewWork(_visibleClientId);
        _workRepository.GetNoTracking(stored.Id).Returns(stored);

        var result = await NewPutHandler().Handle(
            new PutCommand<WorkResource>(NewResource(stored.Id, _hiddenClientId)), CancellationToken.None);

        result.ShouldBeNull();
        await _workRepository.DidNotReceive().Put(Arg.Any<Work>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Put_WithAForgedScenarioToken_KeepsTheStoredMainPlanToken_AndIsStillDayLocked()
    {
        var stored = NewWork(_visibleClientId);
        _workRepository.GetNoTracking(stored.Id).Returns(stored);
        _dayLockService.EnsureNotLockedAsync(Arg.Any<DateOnly>(), Arg.Any<Guid>(), null, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidRequestException("Day is sealed and cannot be modified.")));
        var resource = NewResource(stored.Id, _visibleClientId);
        resource.AnalyseToken = Guid.NewGuid();

        await Should.ThrowAsync<InvalidRequestException>(() => NewPutHandler().Handle(
            new PutCommand<WorkResource>(resource), CancellationToken.None));

        await _dayLockService.Received().EnsureNotLockedAsync(Day, _visibleClientId, null, Arg.Any<CancellationToken>());
        await _workRepository.DidNotReceive().Put(Arg.Any<Work>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Delete_WorkOfHiddenClient_IsRefusedLikeAMissingWork_NothingDeleted()
    {
        var hidden = NewWork(_hiddenClientId);
        _workRepository.Get(hidden.Id).Returns(hidden);
        var handler = new DeleteCommandHandler(
            _workRepository, _clientVisibilityGuard, new ScheduleMapper(), _periodHoursService,
            _scheduleEntriesService, _completionService, _notificationFacade, _cascadeService,
            _groupContextResolver, _dayLockService, _unitOfWork, _overtimeCascadeService,
            Substitute.For<IHttpContextAccessor>(), new ParentWorkLockGuard(new WorkLockLevelService()),
            Substitute.For<ILogger<DeleteCommandHandler>>());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(
            new DeleteWorkCommand(hidden.Id, Day, Day), CancellationToken.None));

        ex.Message.ShouldBe($"Work with ID {hidden.Id} not found.");
        await _workRepository.DidNotReceive().Delete(Arg.Any<Guid>());
        await _cascadeService.DidNotReceive().DeleteChildrenAsync(Arg.Any<Guid>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task BulkAdd_WithOneHiddenClient_IsRefusedAsAWhole_NothingWritten()
    {
        var handler = new BulkAddWorksCommandHandler(
            _workRepository, _clientVisibilityGuard, new ScheduleMapper(), _periodHoursService, _completionService,
            _notificationFacade, Substitute.For<IContainerWorkExpansionService>(), _overtimeCascadeService,
            _dayLockService, _conflictChecker, Substitute.For<IShiftDefaultExpensesApplier>(),
            Substitute.For<ILogger<BulkAddWorksCommandHandler>>());
        var request = new BulkAddWorksRequest
        {
            PeriodStart = Day,
            PeriodEnd = Day,
            Works =
            [
                new BulkWorkItem { ClientId = _visibleClientId, ShiftId = Guid.NewGuid(), CurrentDate = Day },
                new BulkWorkItem { ClientId = _hiddenClientId, ShiftId = Guid.NewGuid(), CurrentDate = Day }
            ]
        };

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new BulkAddWorksCommand(request), CancellationToken.None));

        ex.Message.ShouldNotContain(_hiddenClientId.ToString());
        await _workRepository.DidNotReceive().Add(Arg.Any<Work>());
        await _dayLockService.DidNotReceiveWithAnyArgs().EnsureNoneLockedAsync(default!, default);
        await _completionService.DidNotReceiveWithAnyArgs()
            .SaveBulkAndTrackRangeAsync(default!, default, default, default);
        await _clientVisibilityGuard.Received(1)
            .AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BulkDelete_WorksOfHiddenClients_AreTreatedLikeMissingIds_NotRemoved()
    {
        var visible = NewWork(_visibleClientId);
        var hidden = NewWork(_hiddenClientId);
        var missingId = Guid.NewGuid();
        _workRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>()).Returns(new List<Work> { visible, hidden });
        _periodHoursService.GetPeriodBoundariesAsync(Arg.Any<DateOnly>()).Returns((Day, Day));
        var handler = new BulkDeleteWorksCommandHandler(
            _workRepository, _clientVisibilityGuard, new ScheduleMapper(), _periodHoursService, _completionService,
            _notificationFacade, _overtimeCascadeService, _dayLockService,
            Substitute.For<IHttpContextAccessor>(), new ParentWorkLockGuard(new WorkLockLevelService()),
            Substitute.For<ILogger<BulkDeleteWorksCommandHandler>>());

        var response = await handler.Handle(
            new BulkDeleteWorksCommand(new BulkDeleteWorksRequest { WorkIds = [visible.Id, hidden.Id, missingId] }),
            CancellationToken.None);

        _workRepository.Received(1).Remove(visible);
        _workRepository.DidNotReceive().Remove(hidden);
        response.DeletedIds.ShouldBe(new[] { visible.Id });
        response.FailedCount.ShouldBe(2);
    }

    [Test]
    public async Task Reassign_AwayFromAHiddenClient_IsRefusedLikeAMissingWork_NothingWritten()
    {
        var stored = NewWork(_hiddenClientId);
        _workRepository.GetNoTracking(stored.Id).Returns(stored);

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => NewReassignHandler().Handle(
            new ReassignWorkClientCommand(stored.Id, _visibleClientId), CancellationToken.None));

        ex.Message.ShouldBe($"Work with ID {stored.Id} not found.");
        await _dayLockService.DidNotReceiveWithAnyArgs().EnsureNotLockedAsync(default, default, default, default);
        await _workRepository.DidNotReceive().Get(Arg.Any<Guid>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Reassign_OntoAHiddenClient_IsRefusedLikeAMissingWork_NothingWritten()
    {
        var stored = NewWork(_visibleClientId);
        _workRepository.GetNoTracking(stored.Id).Returns(stored);

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => NewReassignHandler().Handle(
            new ReassignWorkClientCommand(stored.Id, _hiddenClientId), CancellationToken.None));

        ex.Message.ShouldBe($"Work with ID {stored.Id} not found.");
        await _workRepository.DidNotReceive().Get(Arg.Any<Guid>());
        await _cascadeService.DidNotReceiveWithAnyArgs().MoveChildrenAsync(default, default, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Confirm_WorkOfHiddenClient_IsRefusedLikeAMissingWork_NothingSealed()
    {
        var hidden = NewWork(_hiddenClientId);
        _workRepository.Get(hidden.Id).Returns(hidden);
        var lockLevelService = Substitute.For<IWorkLockLevelService>();
        var handler = new ConfirmWorkCommandHandler(
            _workRepository, _clientVisibilityGuard, _unitOfWork, lockLevelService, new ScheduleMapper(),
            Substitute.For<IHttpContextAccessor>(), _cascadeService, Substitute.For<IPeriodAuditLogRepository>(),
            Substitute.For<IUserService>(), Substitute.For<ILogger<ConfirmWorkCommandHandler>>());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new ConfirmWorkCommand(hidden.Id), CancellationToken.None));

        ex.Message.ShouldBe($"Work with ID {hidden.Id} not found.");
        lockLevelService.DidNotReceiveWithAnyArgs().Seal(default!, default, default!, default, default);
        await _workRepository.DidNotReceive().Put(Arg.Any<Work>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Unconfirm_WorkOfHiddenClient_IsRefusedLikeAMissingWork_BeforeTheSealStateCheck()
    {
        var hidden = NewWork(_hiddenClientId);
        hidden.LockLevel = WorkLockLevel.None;
        _workRepository.Get(hidden.Id).Returns(hidden);
        var handler = new UnconfirmWorkCommandHandler(
            _workRepository, _clientVisibilityGuard, _unitOfWork, new WorkLockLevelService(), new ScheduleMapper(),
            Substitute.For<IHttpContextAccessor>(), Substitute.For<ILogger<UnconfirmWorkCommandHandler>>());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new UnconfirmWorkCommand(hidden.Id), CancellationToken.None));

        ex.Message.ShouldBe($"Work with ID {hidden.Id} not found.");
        await _workRepository.DidNotReceive().Put(Arg.Any<Work>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Restore_WorkOfHiddenClient_IsRefusedLikeAMissingWork_NothingRestored()
    {
        var hidden = NewWork(_hiddenClientId);
        hidden.DeletedTime = DateTime.UtcNow;
        _workRepository.GetDeletedAsync(hidden.Id, Arg.Any<CancellationToken>()).Returns(hidden);
        var authorizer = Substitute.For<IWorkRestoreAuthorizer>();
        authorizer.Resolve(Arg.Any<Work>()).Returns(WorkRestoreAccess.Owner);
        var handler = new RestoreWorkCommandHandler(
            _workRepository, _clientVisibilityGuard, new ScheduleMapper(), _periodHoursService,
            _scheduleEntriesService, _completionService, _notificationFacade, _cascadeService,
            Substitute.For<IWorkSofteningRepository>(), _groupContextResolver, _dayLockService, _writeGuard,
            authorizer, _unitOfWork, _overtimeCascadeService, Substitute.For<ILogger<RestoreWorkCommandHandler>>());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new RestoreWorkCommand(hidden.Id), CancellationToken.None));

        ex.Message.ShouldBe($"Deleted work with ID {hidden.Id} not found.");
        await _workRepository.DidNotReceive().RestoreAsync(Arg.Any<Work>());
        await _dayLockService.DidNotReceiveWithAnyArgs().EnsureNotLockedAsync(default, default, default, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task GetContainerChildren_OfAHiddenContainer_IsAnsweredLikeAMissingContainer()
    {
        var hidden = NewWork(_hiddenClientId);
        var childrenReadRepository = Substitute.For<IContainerWorkChildrenReadRepository>();
        childrenReadRepository.GetParentWorkNoTracking(hidden.Id, Arg.Any<CancellationToken>()).Returns(hidden);
        var handler = new GetContainerWorkChildrenQueryHandler(
            childrenReadRepository, _clientVisibilityGuard, new ScheduleMapper(),
            Substitute.For<ILogger<GetContainerWorkChildrenQueryHandler>>());

        var result = await handler.Handle(new GetContainerWorkChildrenQuery(hidden.Id, false), CancellationToken.None);

        result.SubWorks.ShouldBeEmpty();
        result.SubBreaks.ShouldBeEmpty();
        result.SubWorkChanges.ShouldBeEmpty();
        result.ParentStartBase.ShouldBeNull();
        await childrenReadRepository.DidNotReceive()
            .GetChildBreaksWithAbsence(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateContainerChildren_OfAHiddenContainer_IsRefusedLikeAMissingContainer_NothingWritten()
    {
        var hidden = NewWork(_hiddenClientId);
        var (handler, childrenManager) = NewUpdateContainerHandler(hidden);

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(
            new UpdateContainerWorkChildrenCommand(hidden.Id, new UpdateContainerWorkChildrenResource()),
            CancellationToken.None));

        ex.Message.ShouldBe($"Work with ID {hidden.Id} not found.");
        await childrenManager.DidNotReceiveWithAnyArgs()
            .UpdateChildrenAsync(default, default, default, default, default, default!, default!, default!, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task UpdateContainerChildren_ReplacingWithAHiddenClient_IsRefusedLikeAMissingContainer_NothingWritten()
    {
        var container = NewWork(_visibleClientId);
        var (handler, childrenManager) = NewUpdateContainerHandler(container);
        var resource = new UpdateContainerWorkChildrenResource
        {
            SubWorkChanges = [new WorkChangeResource { Id = Guid.NewGuid(), ReplaceClientId = _hiddenClientId }]
        };

        await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(
            new UpdateContainerWorkChildrenCommand(container.Id, resource), CancellationToken.None));

        await childrenManager.DidNotReceiveWithAnyArgs()
            .UpdateChildrenAsync(default, default, default, default, default, default!, default!, default!, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task UpdateContainerChildren_WithASubWorkForAHiddenClient_IsRefusedLikeAMissingContainer_NothingWritten()
    {
        var container = NewWork(_visibleClientId);
        var (handler, childrenManager) = NewUpdateContainerHandler(container);
        var resource = new UpdateContainerWorkChildrenResource
        {
            SubWorks = [NewResource(Guid.NewGuid(), _hiddenClientId)]
        };

        await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(
            new UpdateContainerWorkChildrenCommand(container.Id, resource), CancellationToken.None));

        await childrenManager.DidNotReceiveWithAnyArgs()
            .UpdateChildrenAsync(default, default, default, default, default, default!, default!, default!, default);
        await _clientVisibilityGuard.Received(1)
            .AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ApproveDay_ForAHiddenGroup_IsAnsweredLikeAGroupWithoutEntries_NothingSealed()
    {
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        WorksTestHelpers.GivenUserIsAuthorised(httpContextAccessor, "authorised-user");
        var groupVisibilityGuard = Substitute.For<IGroupVisibilityGuard>();
        groupVisibilityGuard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        var breakRepository = Substitute.For<IBreakRepository>();
        var auditLogRepository = Substitute.For<IPeriodAuditLogRepository>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>())
            .Returns(ci => ci.ArgAt<Func<Task<int>>>(0)());
        var handler = new ApproveDayCommandHandler(
            _workRepository, groupVisibilityGuard, breakRepository, new WorkLockLevelService(), httpContextAccessor,
            auditLogRepository, Substitute.For<IUserService>(), Substitute.For<ISealedDayRepository>(), _unitOfWork,
            Substitute.For<ILogger<ApproveDayCommandHandler>>());

        var affected = await handler.Handle(new ApproveDayCommand(Day, Guid.NewGuid()), CancellationToken.None);

        affected.ShouldBe(0);
        await _workRepository.DidNotReceiveWithAnyArgs().SealByDayAndGroup(default, default, default, default!, default);
        await breakRepository.DidNotReceiveWithAnyArgs().SealByDayAndGroup(default, default, default, default!, default);
        await auditLogRepository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Test]
    public async Task RevokeDayApproval_ForAHiddenGroup_IsAnsweredLikeAGroupWithoutEntries_NothingUnsealed()
    {
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        WorksTestHelpers.GivenUserIsAuthorised(httpContextAccessor, "authorised-user");
        var groupVisibilityGuard = Substitute.For<IGroupVisibilityGuard>();
        groupVisibilityGuard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        var breakRepository = Substitute.For<IBreakRepository>();
        var handler = new RevokeDayApprovalCommandHandler(
            _workRepository, groupVisibilityGuard, breakRepository, new WorkLockLevelService(), httpContextAccessor,
            Substitute.For<ISealedDayRepository>(), Substitute.For<ILogger<RevokeDayApprovalCommandHandler>>());

        var affected = await handler.Handle(new RevokeDayApprovalCommand(Day, Guid.NewGuid()), CancellationToken.None);

        affected.ShouldBe(0);
        await _workRepository.DidNotReceiveWithAnyArgs().UnsealByDayAndGroup(default, default, default, default);
        await breakRepository.DidNotReceiveWithAnyArgs().UnsealByDayAndGroup(default, default, default, default);
    }

    private PutCommandHandler NewPutHandler()
        => new(
            _workRepository, _clientVisibilityGuard, new ScheduleMapper(), _periodHoursService,
            _scheduleEntriesService, _completionService, _notificationFacade, _cascadeService,
            _groupContextResolver, _dayLockService, _unitOfWork, _overtimeCascadeService, _conflictChecker,
            _httpContextAccessor, new ParentWorkLockGuard(new WorkLockLevelService()),
            Substitute.For<ILogger<PutCommandHandler>>());

    private ReassignWorkClientCommandHandler NewReassignHandler()
        => new(
            _workRepository, _clientVisibilityGuard, new ScheduleMapper(), _periodHoursService,
            _scheduleEntriesService, _completionService, _notificationFacade, _cascadeService,
            _groupContextResolver, _dayLockService, _unitOfWork, _overtimeCascadeService, _conflictChecker,
            Substitute.For<ILogger<ReassignWorkClientCommandHandler>>());

    private (UpdateContainerWorkChildrenCommandHandler Handler, IContainerWorkChildrenManager ChildrenManager)
        NewUpdateContainerHandler(Work container)
    {
        var childrenReadRepository = Substitute.For<IContainerWorkChildrenReadRepository>();
        childrenReadRepository.GetParentWorkNoTracking(container.Id, Arg.Any<CancellationToken>()).Returns(container);
        var lockRepository = Substitute.For<IContainerLockRepository>();
        lockRepository.IsHeldBy(
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var childrenManager = Substitute.For<IContainerWorkChildrenManager>();
        var handler = new UpdateContainerWorkChildrenCommandHandler(
            childrenReadRepository, _clientVisibilityGuard, new ScheduleMapper(), _unitOfWork, _notificationFacade,
            lockRepository, Substitute.For<IUserService>(), childrenManager, _overtimeCascadeService,
            Substitute.For<ILogger<UpdateContainerWorkChildrenCommandHandler>>());
        return (handler, childrenManager);
    }

    private static Work NewWork(Guid clientId)
        => new() { Id = Guid.NewGuid(), ClientId = clientId, ShiftId = Guid.NewGuid(), CurrentDate = Day };

    private static WorkResource NewResource(Guid id, Guid clientId)
        => new() { Id = id, ClientId = clientId, ShiftId = Guid.NewGuid(), CurrentDate = Day };
}
