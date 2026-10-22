// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The Work create paths (single POST and bulk add, which also carries place_work) must refuse a day outside the
/// employee's membership or group period exactly like the grid's locked cells and like the reassign path: the guard is
/// asked for every (client, day) cell after the visibility and day-lock checks, a refusal writes nothing, and a day inside
/// the period is written as before. Planner output (wizard, harmonizer, propose_plan) opts out explicitly. A PUT that
/// moves a Work to another day or client is held to the same rule; an edit that keeps the stored cell is not checked, so
/// a Work in a cell locked since stays editable.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class WorkCreatePlanningPeriodTests
{
    private const string OutsideMessage =
        "Anna Meier cannot be planned on 2027-04-12: the day is outside the membership period (2027-05-01 to open).";

    private static readonly DateOnly Day = new(2027, 4, 12);

    private IWorkRepository _workRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IScheduleEntriesService _scheduleEntriesService = null!;
    private IDayLockService _dayLockService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IPreCommitConflictChecker _conflictChecker = null!;
    private IScheduleCompletionService _completionService = null!;
    private IClientPlanningPeriodGuard _planningPeriodGuard = null!;
    private Guid _clientId;

    [SetUp]
    public void SetUp()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _scheduleEntriesService = Substitute.For<IScheduleEntriesService>();
        _dayLockService = Substitute.For<IDayLockService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _conflictChecker = Substitute.For<IPreCommitConflictChecker>();
        _completionService = Substitute.For<IScheduleCompletionService>();
        _planningPeriodGuard = Substitute.For<IClientPlanningPeriodGuard>();
        _clientId = Guid.NewGuid();

        _clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _clientVisibilityGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        ScheduleEntriesStub.ServeNothing(_scheduleEntriesService);
        _conflictChecker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult([]));
    }

    [Test]
    public async Task Post_OnADayOutsideThePlanningPeriod_IsRefused_NothingWritten()
    {
        GivenGuardRefuses();

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewPostHandler().Handle(
            new PostCommand<WorkResource>(NewResource()), CancellationToken.None));

        ex.Message.ShouldBe(OutsideMessage);
        await _planningPeriodGuard.Received(1).EnsureWithinPeriodsAsync(
            Arg.Is<IReadOnlyCollection<(Guid ClientId, DateOnly Date)>>(cells =>
                cells.Single().ClientId == _clientId && cells.Single().Date == Day),
            Arg.Any<CancellationToken>());
        await _workRepository.DidNotReceive().Add(Arg.Any<Work>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Post_ForAHiddenClient_IsAnsweredAsNotFound_BeforeThePeriodGuardCanNameTheEmployee()
    {
        _clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        GivenGuardRefuses();

        await Should.ThrowAsync<KeyNotFoundException>(() => NewPostHandler().Handle(
            new PostCommand<WorkResource>(NewResource()), CancellationToken.None));

        await _planningPeriodGuard.DidNotReceiveWithAnyArgs().EnsureWithinPeriodsAsync(default!, default);
    }

    [Test]
    public async Task Post_OnADayInsideThePlanningPeriod_IsWritten()
    {
        var result = await NewPostHandler().Handle(
            new PostCommand<WorkResource>(NewResource()), CancellationToken.None);

        result.ShouldNotBeNull();
        await _planningPeriodGuard.Received(1).EnsureWithinPeriodsAsync(
            Arg.Any<IReadOnlyCollection<(Guid ClientId, DateOnly Date)>>(), Arg.Any<CancellationToken>());
        await _workRepository.Received(1).Add(Arg.Is<Work>(w => w.ClientId == _clientId && w.CurrentDate == Day));
        await _unitOfWork.Received().CompleteAsync();
    }

    [Test]
    public async Task Post_OfAContainer_AsksThePeriodGuardOnlyForTheParentCell_TheExpansionStillRuns()
    {
        var expansion = Substitute.For<IContainerWorkExpansionService>();

        await NewPostHandler(expansion).Handle(new PostCommand<WorkResource>(NewResource()), CancellationToken.None);

        await _planningPeriodGuard.Received(1).EnsureWithinPeriodsAsync(
            Arg.Is<IReadOnlyCollection<(Guid ClientId, DateOnly Date)>>(cells =>
                cells.Count == 1 && cells.Single().ClientId == _clientId && cells.Single().Date == Day),
            Arg.Any<CancellationToken>());
        await expansion.Received(1).ExpandAsync(Arg.Is<Work>(w => w.ClientId == _clientId), Day);
    }

    [Test]
    public async Task BulkAdd_WithOneDayOutsideThePlanningPeriod_IsRefusedAsAWhole_NothingWritten()
    {
        GivenGuardRefuses();
        var otherClient = Guid.NewGuid();

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewBulkHandler().Handle(
            new BulkAddWorksCommand(NewBulkRequest(_clientId, otherClient, _clientId)), CancellationToken.None));

        ex.Message.ShouldBe(OutsideMessage);
        await _planningPeriodGuard.Received(1).EnsureWithinPeriodsAsync(
            Arg.Is<IReadOnlyCollection<(Guid ClientId, DateOnly Date)>>(cells =>
                cells.Count == 2 && IsCellOf(cells, _clientId) && IsCellOf(cells, otherClient)),
            Arg.Any<CancellationToken>());
        await _workRepository.DidNotReceive().Add(Arg.Any<Work>());
        await _completionService.DidNotReceiveWithAnyArgs()
            .SaveBulkAndTrackRangeAsync(default!, default, default, default);
    }

    [Test]
    public async Task BulkAdd_InsideThePlanningPeriod_IsWritten()
    {
        var response = await NewBulkHandler().Handle(
            new BulkAddWorksCommand(NewBulkRequest(_clientId)), CancellationToken.None);

        response.SuccessCount.ShouldBe(1);
        await _planningPeriodGuard.Received(1).EnsureWithinPeriodsAsync(
            Arg.Any<IReadOnlyCollection<(Guid ClientId, DateOnly Date)>>(), Arg.Any<CancellationToken>());
        await _workRepository.Received(1).Add(Arg.Is<Work>(w => w.ClientId == _clientId && w.CurrentDate == Day));
    }

    [Test]
    public async Task BulkAdd_FromPlannerOutput_DoesNotAskThePeriodGuard_AndIsWritten()
    {
        GivenGuardRefuses();

        var response = await NewBulkHandler().Handle(
            new BulkAddWorksCommand(NewBulkRequest(_clientId), PlanningPeriodCheck.SkipForPlannerOutput),
            CancellationToken.None);

        response.SuccessCount.ShouldBe(1);
        await _planningPeriodGuard.DidNotReceiveWithAnyArgs().EnsureWithinPeriodsAsync(default!, default);
        await _workRepository.Received(1).Add(Arg.Any<Work>());
    }

    [Test]
    public async Task Put_MovingTheWorkToADayOutsideThePlanningPeriod_IsRefused_NothingWritten()
    {
        var stored = GivenStoredWork();
        GivenGuardRefuses();
        var moved = NewResource();
        moved.Id = stored.Id;
        moved.CurrentDate = Day.AddDays(1);

        await Should.ThrowAsync<InvalidRequestException>(() => NewPutHandler().Handle(
            new PutCommand<WorkResource>(moved), CancellationToken.None));

        await _planningPeriodGuard.Received(1).EnsureWithinPeriodsAsync(
            Arg.Is<IReadOnlyCollection<(Guid ClientId, DateOnly Date)>>(cells =>
                cells.Single().ClientId == _clientId && cells.Single().Date == Day.AddDays(1)),
            Arg.Any<CancellationToken>());
        await _workRepository.DidNotReceive().Put(Arg.Any<Work>());
    }

    [Test]
    public async Task Put_MovingTheWorkToAnotherClient_AsksThePeriodGuardForTheNewClient()
    {
        var stored = GivenStoredWork();
        GivenGuardRefuses();
        var otherClient = Guid.NewGuid();
        var moved = NewResource();
        moved.Id = stored.Id;
        moved.ClientId = otherClient;

        await Should.ThrowAsync<InvalidRequestException>(() => NewPutHandler().Handle(
            new PutCommand<WorkResource>(moved), CancellationToken.None));

        await _planningPeriodGuard.Received(1).EnsureWithinPeriodsAsync(
            Arg.Is<IReadOnlyCollection<(Guid ClientId, DateOnly Date)>>(cells => cells.Single().ClientId == otherClient),
            Arg.Any<CancellationToken>());
        await _workRepository.DidNotReceive().Put(Arg.Any<Work>());
    }

    [Test]
    public async Task Put_KeepingTheStoredCell_IsNotChecked_SoAWorkInALockedCellStaysEditable()
    {
        var stored = GivenStoredWork();
        GivenGuardRefuses();
        var edited = NewResource();
        edited.Id = stored.Id;
        edited.Information = "edited";

        await NewPutHandler().Handle(new PutCommand<WorkResource>(edited), CancellationToken.None);

        await _planningPeriodGuard.DidNotReceiveWithAnyArgs().EnsureWithinPeriodsAsync(default!, default);
        await _workRepository.Received(1).Put(Arg.Any<Work>());
    }

    [Test]
    public void BulkAddCommand_EnforcesThePeriodCheck_WhenTheCallerSaysNothing()
    {
        new BulkAddWorksCommand(NewBulkRequest(_clientId)).PeriodCheck.ShouldBe(PlanningPeriodCheck.Enforce);
    }

    private static bool IsCellOf(IReadOnlyCollection<(Guid ClientId, DateOnly Date)> cells, Guid clientId)
        => cells.Any(cell => cell.ClientId == clientId && cell.Date == Day);

    private void GivenGuardRefuses()
    {
        _planningPeriodGuard
            .EnsureWithinPeriodsAsync(Arg.Any<IReadOnlyCollection<(Guid ClientId, DateOnly Date)>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidRequestException(OutsideMessage)));
    }

    private Work GivenStoredWork()
    {
        var stored = new Work { Id = Guid.NewGuid(), ClientId = _clientId, ShiftId = Guid.NewGuid(), CurrentDate = Day };
        _workRepository.GetNoTracking(stored.Id).Returns(stored);
        _workRepository.Put(Arg.Any<Work>()).Returns(call => call.Arg<Work>());
        _conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult([]));
        return stored;
    }

    private PutCommandHandler NewPutHandler()
    {
        var rowAccessGuard = Substitute.For<IScheduleRowAccessGuard>();
        rowAccessGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        return new PutCommandHandler(
            _workRepository, rowAccessGuard, new ScheduleMapper(), Substitute.For<IPeriodHoursService>(),
            _scheduleEntriesService, _completionService, Substitute.For<IWorkNotificationFacade>(),
            Substitute.For<IContainerWorkCascadeService>(), Substitute.For<ISelectedGroupContextResolver>(), _dayLockService,
            _unitOfWork, Substitute.For<IOvertimeCascadeService>(), TestWorkWriteGuard.Over(_conflictChecker),
            Substitute.For<Microsoft.AspNetCore.Http.IHttpContextAccessor>(), Substitute.For<IParentWorkLockGuard>(),
            _planningPeriodGuard, ShiftVisibilityGuards.AllowAll(), Substitute.For<ILogger<PutCommandHandler>>());
    }

    private PostCommandHandler NewPostHandler(IContainerWorkExpansionService? expansion = null) => new(
        _workRepository, _clientVisibilityGuard, new ScheduleMapper(), Substitute.For<IPeriodHoursService>(),
        _scheduleEntriesService, _completionService, Substitute.For<IWorkNotificationFacade>(),
        Substitute.For<IShiftDefaultExpensesApplier>(), expansion ?? Substitute.For<IContainerWorkExpansionService>(),
        Substitute.For<ISelectedGroupContextResolver>(), _dayLockService, _unitOfWork,
        Substitute.For<IOvertimeCascadeService>(), Substitute.For<IWorkWriteGuard>(), _planningPeriodGuard,
        ShiftVisibilityGuards.AllowAll(), Substitute.For<ILogger<PostCommandHandler>>());

    private BulkAddWorksCommandHandler NewBulkHandler() => new(
        _workRepository, _clientVisibilityGuard, new ScheduleMapper(), Substitute.For<IPeriodHoursService>(),
        _completionService, Substitute.For<IWorkNotificationFacade>(), Substitute.For<IContainerWorkExpansionService>(),
        Substitute.For<IOvertimeCascadeService>(), _dayLockService, TestWorkWriteGuard.Over(_conflictChecker),
        Substitute.For<IShiftDefaultExpensesApplier>(), _planningPeriodGuard, ShiftVisibilityGuards.AllowAll(),
        Substitute.For<ILogger<BulkAddWorksCommandHandler>>());

    private WorkResource NewResource()
        => new() { Id = Guid.NewGuid(), ClientId = _clientId, ShiftId = Guid.NewGuid(), CurrentDate = Day };

    private static BulkAddWorksRequest NewBulkRequest(params Guid[] clientIds) => new()
    {
        PeriodStart = Day,
        PeriodEnd = Day,
        Works = clientIds
            .Select(clientId => new BulkWorkItem { ClientId = clientId, ShiftId = Guid.NewGuid(), CurrentDate = Day })
            .ToList()
    };
}
