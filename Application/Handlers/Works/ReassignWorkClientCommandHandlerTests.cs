// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ReassignWorkClientCommandHandler: verifies the dedicated cross-client
/// reassign path (single request, no client-side GET+PUT round trip) mutates ClientId in
/// place, cascades to children via the new client, tracks both the old and new position,
/// and splits the returned schedule entries by client.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class ReassignWorkClientCommandHandlerTests
{
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
    private IPreCommitConflictChecker _conflictChecker = null!;
    private ReassignWorkClientCommandHandler _handler = null!;

    private readonly Guid _workId = Guid.NewGuid();
    private readonly Guid _sourceClientId = Guid.NewGuid();
    private readonly Guid _targetClientId = Guid.NewGuid();
    private readonly Guid _shiftId = Guid.NewGuid();
    private readonly DateOnly _date = new(2027, 3, 1);
    private readonly DateOnly _periodStart = new(2027, 3, 1);
    private readonly DateOnly _periodEnd = new(2027, 3, 31);

    [SetUp]
    public void Setup()
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
        _conflictChecker = Substitute.For<IPreCommitConflictChecker>();
        _conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(),
                Arg.Any<IReadOnlyList<PlannedRemovalRow>>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(PreCommitCheckResult.Empty);

        var existingWork = new Work { Id = _workId, ClientId = _sourceClientId, CurrentDate = _date, ShiftId = _shiftId };
        var trackedWork = new Work { Id = _workId, ClientId = _sourceClientId, CurrentDate = _date, ShiftId = _shiftId };
        _workRepository.GetNoTracking(_workId).Returns(existingWork);
        _workRepository.Get(_workId).Returns(trackedWork);

        _periodHoursService.GetPeriodBoundariesAsync(_date).Returns((_periodStart, _periodEnd));
        _notificationFacade.GetConnectionId().Returns("connection-1");
        _groupContextResolver.ResolveVisibleGroupIdsAsync().Returns((List<Guid>?)null);
        _completionService.SaveAndTrackMoveAsync(
                _targetClientId, _date, _periodStart, _periodEnd, _sourceClientId, _date, null)
            .Returns(new PeriodHoursResource { Hours = 8 });

        var cells = new[]
        {
            new ScheduleCell { ClientId = _targetClientId, EntryId = _shiftId },
            new ScheduleCell { ClientId = _sourceClientId, EntryId = _shiftId },
        };
        _scheduleEntriesService.GetScheduleEntriesQuery(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<List<Guid>?>(), Arg.Any<Guid?>())
            .Returns(new TestAsyncEnumerable<ScheduleCell>(cells));

        _handler = new ReassignWorkClientCommandHandler(
            _workRepository,
            new ScheduleMapper(),
            _periodHoursService,
            _scheduleEntriesService,
            _completionService,
            _notificationFacade,
            _cascadeService,
            _groupContextResolver,
            _dayLockService,
            _unitOfWork,
            _overtimeCascadeService,
            _conflictChecker,
            Substitute.For<ILogger<ReassignWorkClientCommandHandler>>());
    }

    [Test]
    public async Task Handle_ReassignsClientId_AndCascadesChildrenToNewClient()
    {
        var result = await _handler.Handle(new ReassignWorkClientCommand(_workId, _targetClientId), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Work!.ClientId.ShouldBe(_targetClientId);
        await _cascadeService.Received(1).MoveChildrenAsync(_workId, _date, _targetClientId);
    }

    [Test]
    public async Task Handle_ChecksDayLock_ForBothOldAndNewClient()
    {
        await _handler.Handle(new ReassignWorkClientCommand(_workId, _targetClientId), CancellationToken.None);

        await _dayLockService.Received(1).EnsureNotLockedAsync(_date, _sourceClientId, null, Arg.Any<CancellationToken>());
        await _dayLockService.Received(1).EnsureNotLockedAsync(_date, _targetClientId, null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_TracksBothPositions_ViaSaveAndTrackMoveAsync()
    {
        await _handler.Handle(new ReassignWorkClientCommand(_workId, _targetClientId), CancellationToken.None);

        await _completionService.Received(1).SaveAndTrackMoveAsync(
            _targetClientId, _date, _periodStart, _periodEnd, _sourceClientId, _date, null);
    }

    [Test]
    public async Task Handle_SplitsScheduleEntries_ByTargetAndSourceClient()
    {
        var result = await _handler.Handle(new ReassignWorkClientCommand(_workId, _targetClientId), CancellationToken.None);

        result!.Work!.ScheduleEntries.Count.ShouldBe(1);
        result.Work!.ScheduleEntries[0].ClientId.ShouldBe(_targetClientId);
        result.SourceScheduleEntries.Count.ShouldBe(1);
        result.SourceScheduleEntries[0].ClientId.ShouldBe(_sourceClientId);
    }

    [Test]
    public async Task Handle_ThrowsKeyNotFound_WhenWorkDoesNotExist()
    {
        var missingId = Guid.NewGuid();
        _workRepository.GetNoTracking(missingId).Returns((Work?)null);

        Func<Task> act = async () => await _handler.Handle(new ReassignWorkClientCommand(missingId, _targetClientId), CancellationToken.None);

        await Should.ThrowAsync<KeyNotFoundException>(act);
    }

    [Test]
    public async Task Handle_ChecksTheTargetClient_AndVacatesTheSourceRow()
    {
        await _handler.Handle(new ReassignWorkClientCommand(_workId, _targetClientId), CancellationToken.None);

        await _conflictChecker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows =>
                rows.Count == 1 && rows[0].ClientId == _targetClientId && rows[0].Date == _date),
            Arg.Is<IReadOnlyList<PlannedRemovalRow>>(rows =>
                rows.Count == 1 && rows[0].ClientId == _sourceClientId && rows[0].WorkId == _workId),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_Reassigns_WhenTargetClientWouldOnlyCollide()
    {
        GivenConflictCheckReturns(CollisionError());

        var result = await _handler.Handle(
            new ReassignWorkClientCommand(_workId, _targetClientId), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Work!.ClientId.ShouldBe(_targetClientId);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_ThrowsConflict_WhenTargetClientHasANonOverridableStructuralError()
    {
        GivenConflictCheckReturns(HardBlockingError());

        Func<Task> act = async () => await _handler.Handle(
            new ReassignWorkClientCommand(_workId, _targetClientId), CancellationToken.None);

        (await Should.ThrowAsync<ConflictException>(act)).Message.ShouldContain("blocked");
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_Reassigns_WhenOnlyABlockModeEscalationIsReported()
    {
        GivenConflictCheckReturns(EscalatedError());

        var result = await _handler.Handle(
            new ReassignWorkClientCommand(_workId, _targetClientId), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Work!.ClientId.ShouldBe(_targetClientId);
    }

    [Test]
    public async Task Handle_SkipsTheCollisionCheck_ForScenarioWork()
    {
        var scenarioToken = Guid.NewGuid();
        _workRepository.GetNoTracking(_workId).Returns(new Work
        {
            Id = _workId,
            ClientId = _sourceClientId,
            CurrentDate = _date,
            ShiftId = _shiftId,
            AnalyseToken = scenarioToken,
        });

        await _handler.Handle(new ReassignWorkClientCommand(_workId, _targetClientId), CancellationToken.None);

        await _conflictChecker.DidNotReceiveWithAnyArgs().CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(),
            Arg.Any<IReadOnlyList<PlannedRemovalRow>>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_SkipsTheCollisionCheck_ForAContainerChild()
    {
        _workRepository.GetNoTracking(_workId).Returns(new Work
        {
            Id = _workId,
            ClientId = _sourceClientId,
            CurrentDate = _date,
            ShiftId = _shiftId,
            ParentWorkId = Guid.NewGuid(),
        });

        await _handler.Handle(new ReassignWorkClientCommand(_workId, _targetClientId), CancellationToken.None);

        await _conflictChecker.DidNotReceiveWithAnyArgs().CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(),
            Arg.Any<IReadOnlyList<PlannedRemovalRow>>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }

    private void GivenConflictCheckReturns(params ScheduleValidationNotificationDto[] conflicts)
    {
        _conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(),
                Arg.Any<IReadOnlyList<PlannedRemovalRow>>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult(conflicts));
    }

    private ScheduleValidationNotificationDto CollisionError() => new()
    {
        Type = ScheduleValidationType.Error,
        ClientId = _targetClientId,
        Date = _date,
        Comment = "schedule.error-list.collision",
    };

    private ScheduleValidationNotificationDto HardBlockingError() => new()
    {
        Type = ScheduleValidationType.Error,
        ClientId = _targetClientId,
        Date = _date,
        Comment = "schedule.error-list.missing-mandatory-qualification",
    };

    private ScheduleValidationNotificationDto EscalatedError() => new()
    {
        Type = ScheduleValidationType.Error,
        ClientId = _targetClientId,
        Date = _date,
        Comment = "schedule.error-list.rest-violation",
        CommentParams = new Dictionary<string, string>
        {
            [ComplianceRuleNames.EnforcementRuleParamKey] = ComplianceRuleNames.MinRestHours,
        },
    };
}
