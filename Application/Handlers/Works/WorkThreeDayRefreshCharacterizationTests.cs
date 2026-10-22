// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization of the three-day refresh window the Work handlers (create, update, delete, restore, reassign)
/// answer with: the schedule entries are requested for the day before to the day after the Work, with the caller's
/// visible group list and the Work's scenario token passed through unchanged, and only the rows of the Work's client
/// (for reassign: target client rows and source client rows separately) reach the response, in stub order.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class WorkThreeDayRefreshCharacterizationTests
{
    private static readonly DateOnly Day = new(2027, 5, 12);
    private static readonly DateOnly PeriodStart = new(2027, 5, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 5, 31);

    private IWorkRepository _workRepository = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private IScheduleEntriesService _scheduleEntriesService = null!;
    private IScheduleCompletionService _completionService = null!;
    private IWorkNotificationFacade _notificationFacade = null!;
    private IContainerWorkCascadeService _cascadeService = null!;
    private ISelectedGroupContextResolver _groupContextResolver = null!;
    private IDayLockService _dayLockService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IOvertimeCascadeService _overtimeCascadeService = null!;
    private IWorkWriteGuard _writeGuard = null!;
    private IClientPlanningPeriodGuard _planningPeriodGuard = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IWorkRestoreAuthorizer _authorizer = null!;

    private List<Guid> _visibleGroupIds = null!;
    private Guid _analyseToken;
    private Guid _clientId;
    private Guid _sourceClientId;
    private Guid _otherClientId;
    private Guid _shiftId;
    private Guid _workId;

    [SetUp]
    public void SetUp()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _scheduleEntriesService = Substitute.For<IScheduleEntriesService>();
        _completionService = Substitute.For<IScheduleCompletionService>();
        _notificationFacade = Substitute.For<IWorkNotificationFacade>();
        _cascadeService = Substitute.For<IContainerWorkCascadeService>();
        _groupContextResolver = Substitute.For<ISelectedGroupContextResolver>();
        _dayLockService = Substitute.For<IDayLockService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _overtimeCascadeService = Substitute.For<IOvertimeCascadeService>();
        _writeGuard = Substitute.For<IWorkWriteGuard>();
        _planningPeriodGuard = Substitute.For<IClientPlanningPeriodGuard>();
        _clientVisibilityGuard = TestGroupWriteVisibility.AllClientsVisible();
        _authorizer = Substitute.For<IWorkRestoreAuthorizer>();
        _authorizer.Resolve(Arg.Any<Work>()).Returns(WorkRestoreAccess.Owner);

        _visibleGroupIds = [Guid.NewGuid(), Guid.NewGuid()];
        _analyseToken = Guid.NewGuid();
        _clientId = Guid.NewGuid();
        _sourceClientId = Guid.NewGuid();
        _otherClientId = Guid.NewGuid();
        _shiftId = Guid.NewGuid();
        _workId = Guid.NewGuid();

        _groupContextResolver.ResolveVisibleGroupIdsAsync().Returns(_visibleGroupIds);
        _periodHoursService.GetPeriodBoundariesAsync(Day).Returns((PeriodStart, PeriodEnd));
        _notificationFacade.GetConnectionId().Returns("connection-1");
        _completionService
            .SaveAndTrackAsync(
                Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>())
            .Returns(new PeriodHoursResource { Hours = 8 });
        _completionService
            .SaveAndTrackMoveAsync(
                Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<Guid?>(), Arg.Any<DateOnly?>(), Arg.Any<Guid?>())
            .Returns((new PeriodHoursResource { Hours = 8 }, (PeriodHoursResource?)new PeriodHoursResource { Hours = 3 }));
        _workRepository.Put(Arg.Any<Work>()).Returns(call => call.Arg<Work>());
    }

    [Test]
    public async Task Post_RequestsTheThreeDayWindowWithGroupsAndToken_AndReturnsOnlyTheClientRowsInStubOrder()
    {
        var rows = GivenScheduleEntries(_clientId, _otherClientId);
        var resource = new WorkResource
        {
            Id = _workId, ClientId = _clientId, ShiftId = _shiftId, CurrentDate = Day, AnalyseToken = _analyseToken
        };

        var result = await NewPostHandler().Handle(new PostCommand<WorkResource>(resource), CancellationToken.None);

        ThenThreeDayWindowWasRequested();
        result!.ScheduleEntries.Select(e => e.Id).ShouldBe(rows.Own);
    }

    [Test]
    public async Task Put_RequestsTheThreeDayWindowWithGroupsAndToken_AndReturnsOnlyTheClientRowsInStubOrder()
    {
        var rows = GivenScheduleEntries(_clientId, _otherClientId);
        _workRepository.GetNoTracking(_workId).Returns(NewStoredWork());
        var resource = new WorkResource
        {
            Id = _workId, ClientId = _clientId, ShiftId = _shiftId, CurrentDate = Day
        };

        var result = await NewPutHandler().Handle(new PutCommand<WorkResource>(resource), CancellationToken.None);

        ThenThreeDayWindowWasRequested();
        result!.ScheduleEntries.Select(e => e.Id).ShouldBe(rows.Own);
    }

    [Test]
    public async Task Delete_RequestsTheThreeDayWindowWithGroupsAndToken_AndReturnsOnlyTheClientRowsInStubOrder()
    {
        var rows = GivenScheduleEntries(_clientId, _otherClientId);
        _workRepository.Get(_workId).Returns(NewStoredWork());

        var result = await NewDeleteHandler().Handle(new DeleteWorkCommand(_workId, Day, Day), CancellationToken.None);

        ThenThreeDayWindowWasRequested();
        result!.ScheduleEntries.Select(e => e.Id).ShouldBe(rows.Own);
    }

    [Test]
    public async Task Restore_RequestsTheThreeDayWindowWithGroupsAndToken_AndReturnsOnlyTheClientRowsInStubOrder()
    {
        var rows = GivenScheduleEntries(_clientId, _otherClientId);
        var deleted = NewStoredWork();
        deleted.IsDeleted = true;
        deleted.DeletedTime = new DateTime(2027, 5, 12, 12, 0, 0, DateTimeKind.Utc);
        deleted.CurrentUserDeleted = "user-1";
        _workRepository.GetDeletedAsync(_workId, Arg.Any<CancellationToken>()).Returns(deleted);

        var result = await NewRestoreHandler().Handle(new RestoreWorkCommand(_workId), CancellationToken.None);

        ThenThreeDayWindowWasRequested();
        result!.ScheduleEntries.Select(e => e.Id).ShouldBe(rows.Own);
    }

    [Test]
    public async Task Reassign_RequestsTheThreeDayWindowWithGroupsAndToken_AndSplitsTargetAndSourceRows_DroppingOtherClients()
    {
        var targetRows = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var sourceRows = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var otherRows = new[] { Guid.NewGuid(), Guid.NewGuid() };
        GivenScheduleEntries(
            Cell(_clientId, targetRows[0]),
            Cell(_sourceClientId, sourceRows[0]),
            Cell(_otherClientId, otherRows[0]),
            Cell(_clientId, targetRows[1]),
            Cell(_otherClientId, otherRows[1]),
            Cell(_sourceClientId, sourceRows[1]));
        _workRepository.GetNoTracking(_workId).Returns(NewStoredWork(_sourceClientId));
        _workRepository.Get(_workId).Returns(NewStoredWork(_sourceClientId));

        var result = await NewReassignHandler().Handle(
            new ReassignWorkClientCommand(_workId, _clientId), CancellationToken.None);

        ThenThreeDayWindowWasRequested();
        result!.Work!.ScheduleEntries.Select(e => e.Id).ShouldBe(targetRows);
        result.SourceScheduleEntries.Select(e => e.Id).ShouldBe(sourceRows);
    }

    private (Guid[] Own, Guid[] Foreign) GivenScheduleEntries(Guid ownClientId, Guid foreignClientId)
    {
        var own = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var foreign = new[] { Guid.NewGuid(), Guid.NewGuid() };
        GivenScheduleEntries(
            Cell(ownClientId, own[0]),
            Cell(foreignClientId, foreign[0]),
            Cell(ownClientId, own[1]),
            Cell(foreignClientId, foreign[1]));
        return (own, foreign);
    }

    private void GivenScheduleEntries(params ScheduleCell[] cells)
    {
        _scheduleEntriesService
            .GetScheduleEntriesQuery(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<List<Guid>?>(), Arg.Any<Guid?>())
            .Returns(_ => new TestAsyncEnumerable<ScheduleCell>(cells));
    }

    private void ThenThreeDayWindowWasRequested()
    {
        _scheduleEntriesService.Received(1).GetScheduleEntriesQuery(
            Day.AddDays(-1), Day.AddDays(1), _visibleGroupIds, _analyseToken);
    }

    private static ScheduleCell Cell(Guid clientId, Guid id)
        => new() { Id = id, ClientId = clientId, EntryId = Guid.NewGuid() };

    private Work NewStoredWork(Guid? clientId = null)
        => new()
        {
            Id = _workId,
            ClientId = clientId ?? _clientId,
            ShiftId = _shiftId,
            CurrentDate = Day,
            AnalyseToken = _analyseToken
        };

    private PostCommandHandler NewPostHandler()
        => new(
            _workRepository, _clientVisibilityGuard, new ScheduleMapper(), _periodHoursService,
            _scheduleEntriesService, _completionService, _notificationFacade,
            Substitute.For<IShiftDefaultExpensesApplier>(), Substitute.For<IContainerWorkExpansionService>(),
            _groupContextResolver, _dayLockService, _unitOfWork, _overtimeCascadeService, _writeGuard,
            _planningPeriodGuard, ShiftVisibilityGuards.AllowAll(), Substitute.For<ILogger<PostCommandHandler>>());

    private PutCommandHandler NewPutHandler()
        => new(
            _workRepository, TestScheduleRowAccess.Over(_clientVisibilityGuard), new ScheduleMapper(),
            _periodHoursService, _scheduleEntriesService, _completionService, _notificationFacade, _cascadeService,
            _groupContextResolver, _dayLockService, _unitOfWork, _overtimeCascadeService, _writeGuard,
            Substitute.For<IHttpContextAccessor>(), new ParentWorkLockGuard(new WorkLockLevelService()),
            _planningPeriodGuard, ShiftVisibilityGuards.AllowAll(), Substitute.For<ILogger<PutCommandHandler>>());

    private DeleteCommandHandler NewDeleteHandler()
        => new(
            _workRepository, TestScheduleRowAccess.Over(_clientVisibilityGuard), new ScheduleMapper(),
            _periodHoursService, _scheduleEntriesService, _completionService, _notificationFacade, _cascadeService,
            _groupContextResolver, _dayLockService, _unitOfWork, _overtimeCascadeService,
            Substitute.For<IHttpContextAccessor>(), new ParentWorkLockGuard(new WorkLockLevelService()),
            Substitute.For<ILogger<DeleteCommandHandler>>());

    private RestoreWorkCommandHandler NewRestoreHandler()
        => new(
            _workRepository, TestScheduleRowAccess.Over(_clientVisibilityGuard), new ScheduleMapper(),
            _periodHoursService, _scheduleEntriesService, _completionService, _notificationFacade, _cascadeService,
            Substitute.For<IWorkSofteningRepository>(), _groupContextResolver, _dayLockService, _writeGuard,
            _authorizer, _unitOfWork, _overtimeCascadeService, Substitute.For<ILogger<RestoreWorkCommandHandler>>());

    private ReassignWorkClientCommandHandler NewReassignHandler()
        => new(
            _workRepository, TestScheduleRowAccess.Over(_clientVisibilityGuard), new ScheduleMapper(),
            _periodHoursService, _scheduleEntriesService, _completionService, _notificationFacade, _cascadeService,
            _groupContextResolver, _dayLockService, _unitOfWork, _overtimeCascadeService, _writeGuard,
            Substitute.For<IHttpContextAccessor>(), new ParentWorkLockGuard(new WorkLockLevelService()),
            _planningPeriodGuard, Substitute.For<ILogger<ReassignWorkClientCommandHandler>>());
}
