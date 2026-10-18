// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The Work write paths (POST, PUT, bulk add, container children) must refuse a Work on a shift outside the caller's
/// group visibility exactly like a Work on a shift that does not exist (KeyNotFoundException with the shared
/// "Shift with ID ... not found" text), before any other guard runs and before anything is written. Only the shift a
/// write sets or changes is checked: a PUT that keeps its stored shift and a container save that keeps a sub-work's
/// stored shift do not ask the guard, so existing Works on shifts that have since become hidden stay editable.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Shifts;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class WorkShiftVisibilityTests
{
    private static readonly DateOnly Day = new(2027, 4, 12);

    private IWorkRepository _workRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IScheduleEntriesService _scheduleEntriesService = null!;
    private IDayLockService _dayLockService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IPreCommitConflictChecker _conflictChecker = null!;
    private IWorkWriteGuard _writeGuard = null!;
    private IShiftVisibilityGuard _shiftGuard = null!;
    private Guid _clientId;
    private Guid _visibleShiftId;
    private Guid _hiddenShiftId;

    [SetUp]
    public void SetUp()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _scheduleEntriesService = Substitute.For<IScheduleEntriesService>();
        _dayLockService = Substitute.For<IDayLockService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _conflictChecker = Substitute.For<IPreCommitConflictChecker>();
        _writeGuard = Substitute.For<IWorkWriteGuard>();
        _clientId = Guid.NewGuid();
        _visibleShiftId = Guid.NewGuid();
        _hiddenShiftId = Guid.NewGuid();
        _shiftGuard = Hiding(_hiddenShiftId);

        _clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _clientVisibilityGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _scheduleEntriesService.GetScheduleEntriesQuery(default, default, default, default)
            .ReturnsForAnyArgs(_ => new TestAsyncEnumerable<ScheduleCell>(Enumerable.Empty<ScheduleCell>()));
        _conflictChecker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult([]));
        _conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult([]));
        _workRepository.Put(Arg.Any<Work>()).Returns(call => call.Arg<Work>());
    }

    [Test]
    public async Task Post_OnAHiddenShift_IsRefusedLikeAMissingShift_BeforeAnyOtherGuard_NothingWritten()
    {
        var handler = NewPostHandler();

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(
            new PostCommand<WorkResource>(NewResource(Guid.NewGuid(), _hiddenShiftId)), CancellationToken.None));

        ex.Message.ShouldBe(string.Format(ShiftVisibilityMessages.ShiftNotFound, _hiddenShiftId));
        await _workRepository.DidNotReceive().Add(Arg.Any<Work>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
        await _dayLockService.DidNotReceiveWithAnyArgs().EnsureNotLockedAsync(default, default, default, default);
        await _writeGuard.DidNotReceiveWithAnyArgs().EnsureNoSporadicConflictAsync(default!, default);
        await _writeGuard.DidNotReceiveWithAnyArgs().EnsureNoHardBlockingConflictAsync(default!, default);
    }

    [Test]
    public async Task Post_OnAVisibleShift_IsChecked_AndWritten()
    {
        await NewPostHandler().Handle(
            new PostCommand<WorkResource>(NewResource(Guid.NewGuid(), _visibleShiftId)), CancellationToken.None);

        await _shiftGuard.Received(1).IsVisibleAsync(_visibleShiftId, Arg.Any<CancellationToken>());
        await _workRepository.Received(1).Add(Arg.Is<Work>(w => w.ShiftId == _visibleShiftId));
    }

    [Test]
    public async Task Put_MovingOntoAHiddenShift_IsRefusedLikeAMissingShift_BeforeAnyOtherGuard_NothingWritten()
    {
        var stored = GivenStoredWork(_visibleShiftId);

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => NewPutHandler().Handle(
            new PutCommand<WorkResource>(NewResource(stored.Id, _hiddenShiftId)), CancellationToken.None));

        ex.Message.ShouldBe(string.Format(ShiftVisibilityMessages.ShiftNotFound, _hiddenShiftId));
        await _workRepository.DidNotReceive().Put(Arg.Any<Work>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
        await _dayLockService.DidNotReceiveWithAnyArgs().EnsureNotLockedAsync(default, default, default, default);
        await _conflictChecker.DidNotReceiveWithAnyArgs().CheckAsync(default!, default!, default, default);
    }

    [Test]
    public async Task Put_KeepingAStoredHiddenShift_DoesNotAskTheGuard_AndIsWritten()
    {
        var stored = GivenStoredWork(_hiddenShiftId);

        await NewPutHandler().Handle(
            new PutCommand<WorkResource>(NewResource(stored.Id, _hiddenShiftId)), CancellationToken.None);

        await _shiftGuard.DidNotReceiveWithAnyArgs().IsVisibleAsync(default(Guid), default);
        await _workRepository.Received(1).Put(Arg.Is<Work>(w => w.ShiftId == _hiddenShiftId));
    }

    [Test]
    public async Task Put_MovingOntoAVisibleShift_IsChecked_AndWritten()
    {
        var stored = GivenStoredWork(_hiddenShiftId);

        await NewPutHandler().Handle(
            new PutCommand<WorkResource>(NewResource(stored.Id, _visibleShiftId)), CancellationToken.None);

        await _shiftGuard.Received(1).IsVisibleAsync(_visibleShiftId, Arg.Any<CancellationToken>());
        await _workRepository.Received(1).Put(Arg.Is<Work>(w => w.ShiftId == _visibleShiftId));
    }

    [Test]
    public async Task BulkAdd_WithOneHiddenShift_IsRefusedAsAWholeLikeAMissingShift_WithOneQuery_NothingWritten()
    {
        var request = BulkRequest(_visibleShiftId, _hiddenShiftId, _visibleShiftId);

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => NewBulkHandler().Handle(new BulkAddWorksCommand(request), CancellationToken.None));

        ex.Message.ShouldBe(string.Format(ShiftVisibilityMessages.ShiftNotFound, _hiddenShiftId));
        await _shiftGuard.Received(1).FindFirstHiddenAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2), Arg.Any<CancellationToken>());
        await _workRepository.DidNotReceive().Add(Arg.Any<Work>());
        await _dayLockService.DidNotReceiveWithAnyArgs().EnsureNoneLockedAsync(default!, default);
    }

    [Test]
    public async Task BulkAdd_OnVisibleShifts_IsWritten()
    {
        var response = await NewBulkHandler().Handle(
            new BulkAddWorksCommand(BulkRequest(_visibleShiftId, _visibleShiftId)), CancellationToken.None);

        response.SuccessCount.ShouldBe(2);
        await _workRepository.Received(2).Add(Arg.Any<Work>());
    }

    [Test]
    public async Task UpdateContainerChildren_NewSubWorkOnAHiddenShift_IsRefusedLikeAMissingShift_NothingWritten()
    {
        var container = NewWork(_visibleShiftId);
        var (handler, childrenManager, readRepository) = NewUpdateContainerHandler(container, new Dictionary<Guid, Guid>());
        var resource = new UpdateContainerWorkChildrenResource
        {
            SubWorks = [NewResource(Guid.Empty, _hiddenShiftId), NewResource(Guid.Empty, _visibleShiftId)]
        };

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(
            new UpdateContainerWorkChildrenCommand(container.Id, resource), CancellationToken.None));

        ex.Message.ShouldBe(string.Format(ShiftVisibilityMessages.ShiftNotFound, _hiddenShiftId));
        await _shiftGuard.Received(1).FilterVisibleAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2), Arg.Any<CancellationToken>());
        await childrenManager.DidNotReceiveWithAnyArgs()
            .UpdateChildrenAsync(default, default, default, default, default, default!, default!, default!, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task UpdateContainerChildren_ChangingASubWorkOntoAHiddenShift_IsRefused_NothingWritten()
    {
        var container = NewWork(_visibleShiftId);
        var subWorkId = Guid.NewGuid();
        var (handler, childrenManager, _) = NewUpdateContainerHandler(
            container, new Dictionary<Guid, Guid> { [subWorkId] = _visibleShiftId });
        var resource = new UpdateContainerWorkChildrenResource { SubWorks = [NewResource(subWorkId, _hiddenShiftId)] };

        await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(
            new UpdateContainerWorkChildrenCommand(container.Id, resource), CancellationToken.None));

        await childrenManager.DidNotReceiveWithAnyArgs()
            .UpdateChildrenAsync(default, default, default, default, default, default!, default!, default!, default);
    }

    [Test]
    public async Task UpdateContainerChildren_UnchangedSubWorkOnAHiddenShift_StaysSaveable()
    {
        var container = NewWork(_visibleShiftId);
        var subWorkId = Guid.NewGuid();
        var (handler, childrenManager, _) = NewUpdateContainerHandler(
            container, new Dictionary<Guid, Guid> { [subWorkId] = _hiddenShiftId });
        var resource = new UpdateContainerWorkChildrenResource
        {
            SubWorks = [NewResource(subWorkId, _hiddenShiftId), NewResource(Guid.Empty, _visibleShiftId)]
        };

        await handler.Handle(new UpdateContainerWorkChildrenCommand(container.Id, resource), CancellationToken.None);

        await childrenManager.ReceivedWithAnyArgs(1)
            .UpdateChildrenAsync(default, default, default, default, default, default!, default!, default!, default);
    }

    [Test]
    public async Task UpdateContainerChildren_AllShiftsVisible_DoesNotReadTheStoredSubWorks()
    {
        var container = NewWork(_visibleShiftId);
        var (handler, childrenManager, readRepository) = NewUpdateContainerHandler(container, new Dictionary<Guid, Guid>());
        var resource = new UpdateContainerWorkChildrenResource { SubWorks = [NewResource(Guid.Empty, _visibleShiftId)] };

        await handler.Handle(new UpdateContainerWorkChildrenCommand(container.Id, resource), CancellationToken.None);

        await readRepository.DidNotReceiveWithAnyArgs().GetChildWorkShiftIds(default, default);
        await childrenManager.ReceivedWithAnyArgs(1)
            .UpdateChildrenAsync(default, default, default, default, default, default!, default!, default!, default);
    }

    private static IShiftVisibilityGuard Hiding(Guid hiddenShiftId)
    {
        var guard = Substitute.For<IShiftVisibilityGuard>();
        guard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Guid>(0) != hiddenShiftId);
        guard.FindFirstHiddenAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyCollection<Guid>>(0).Contains(hiddenShiftId) ? hiddenShiftId : (Guid?)null);
        guard.FilterVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlySet<Guid>)call.ArgAt<IReadOnlyCollection<Guid>>(0)
                .Where(id => id != hiddenShiftId)
                .ToHashSet());
        return guard;
    }

    private Work GivenStoredWork(Guid shiftId)
    {
        var stored = NewWork(shiftId);
        _workRepository.GetNoTracking(stored.Id).Returns(stored);
        return stored;
    }

    private BulkAddWorksRequest BulkRequest(params Guid[] shiftIds) => new()
    {
        PeriodStart = Day,
        PeriodEnd = Day,
        Works = shiftIds
            .Select(shiftId => new BulkWorkItem { ClientId = _clientId, ShiftId = shiftId, CurrentDate = Day })
            .ToList()
    };

    private PostCommandHandler NewPostHandler() => new(
        _workRepository, _clientVisibilityGuard, new ScheduleMapper(), Substitute.For<IPeriodHoursService>(),
        _scheduleEntriesService, Substitute.For<IScheduleCompletionService>(), Substitute.For<IWorkNotificationFacade>(),
        Substitute.For<IShiftDefaultExpensesApplier>(), Substitute.For<IContainerWorkExpansionService>(),
        Substitute.For<ISelectedGroupContextResolver>(), _dayLockService, _unitOfWork,
        Substitute.For<IOvertimeCascadeService>(), _writeGuard, Substitute.For<IClientPlanningPeriodGuard>(),
        _shiftGuard, Substitute.For<ILogger<PostCommandHandler>>());

    private PutCommandHandler NewPutHandler() => new(
        _workRepository, TestScheduleRowAccess.Over(_clientVisibilityGuard), new ScheduleMapper(),
        Substitute.For<IPeriodHoursService>(), _scheduleEntriesService, Substitute.For<IScheduleCompletionService>(),
        Substitute.For<IWorkNotificationFacade>(), Substitute.For<IContainerWorkCascadeService>(),
        Substitute.For<ISelectedGroupContextResolver>(), _dayLockService, _unitOfWork,
        Substitute.For<IOvertimeCascadeService>(), TestWorkWriteGuard.Over(_conflictChecker), Substitute.For<IHttpContextAccessor>(),
        new ParentWorkLockGuard(new WorkLockLevelService()), Substitute.For<IClientPlanningPeriodGuard>(),
        _shiftGuard, Substitute.For<ILogger<PutCommandHandler>>());

    private BulkAddWorksCommandHandler NewBulkHandler() => new(
        _workRepository, _clientVisibilityGuard, new ScheduleMapper(), Substitute.For<IPeriodHoursService>(),
        Substitute.For<IScheduleCompletionService>(), Substitute.For<IWorkNotificationFacade>(),
        Substitute.For<IContainerWorkExpansionService>(), Substitute.For<IOvertimeCascadeService>(), _dayLockService,
        TestWorkWriteGuard.Over(_conflictChecker), Substitute.For<IShiftDefaultExpensesApplier>(), Substitute.For<IClientPlanningPeriodGuard>(),
        _shiftGuard, Substitute.For<ILogger<BulkAddWorksCommandHandler>>());

    private (UpdateContainerWorkChildrenCommandHandler Handler, IContainerWorkChildrenManager ChildrenManager,
        IContainerWorkChildrenReadRepository ReadRepository) NewUpdateContainerHandler(
            Work container, IReadOnlyDictionary<Guid, Guid> storedSubWorkShifts)
    {
        var childrenReadRepository = Substitute.For<IContainerWorkChildrenReadRepository>();
        childrenReadRepository.GetParentWorkNoTracking(container.Id, Arg.Any<CancellationToken>()).Returns(container);
        childrenReadRepository.GetChildWorkShiftIds(container.Id, Arg.Any<CancellationToken>()).Returns(storedSubWorkShifts);
        childrenReadRepository.GetChildWorksWithShiftClient(container.Id, Arg.Any<CancellationToken>()).Returns(new List<Work>());
        childrenReadRepository.GetChildBreaksWithAbsence(container.Id, Arg.Any<CancellationToken>()).Returns(new List<Break>());
        childrenReadRepository.GetWorkChangesForWorks(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkChange>());
        var lockRepository = Substitute.For<IContainerLockRepository>();
        lockRepository.IsHeldBy(
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var childrenManager = Substitute.For<IContainerWorkChildrenManager>();
        var handler = new UpdateContainerWorkChildrenCommandHandler(
            childrenReadRepository, TestScheduleRowAccess.Over(_clientVisibilityGuard), new ScheduleMapper(), _unitOfWork,
            Substitute.For<IWorkNotificationFacade>(), lockRepository, Substitute.For<IUserService>(), childrenManager,
            Substitute.For<IOvertimeCascadeService>(), _shiftGuard,
            Substitute.For<ILogger<UpdateContainerWorkChildrenCommandHandler>>());
        return (handler, childrenManager, childrenReadRepository);
    }

    private Work NewWork(Guid shiftId)
        => new() { Id = Guid.NewGuid(), ClientId = _clientId, ShiftId = shiftId, CurrentDate = Day };

    private WorkResource NewResource(Guid id, Guid shiftId)
        => new() { Id = id, ClientId = _clientId, ShiftId = shiftId, CurrentDate = Day };
}
