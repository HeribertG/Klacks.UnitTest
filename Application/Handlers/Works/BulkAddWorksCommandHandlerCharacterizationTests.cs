// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization of BulkAddWorksCommandHandler.Handle (round 12 split): the order of guards, writes and
/// notifications, the merged per-client period range, the bulk and per-client scenario tokens, the deduplicated
/// affected shifts and the response contents, plus the hard-blocking refusal and the empty request.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class BulkAddWorksCommandHandlerCharacterizationTests
{
    private const string ConnectionId = "conn-1";
    private const string RefusalCode = "TEST_REFUSAL";
    private const string RefusalMessage = "Refused for the test.";

    private static readonly DateOnly AprilDay = new(2027, 4, 12);
    private static readonly DateOnly MayDay = new(2027, 5, 3);
    private static readonly DateOnly AprilStart = new(2027, 4, 1);
    private static readonly DateOnly AprilEnd = new(2027, 4, 30);
    private static readonly DateOnly MayStart = new(2027, 5, 1);
    private static readonly DateOnly MayEnd = new(2027, 5, 31);

    private IWorkRepository _workRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private IScheduleCompletionService _completionService = null!;
    private IWorkNotificationFacade _notificationFacade = null!;
    private IContainerWorkExpansionService _expansionService = null!;
    private IOvertimeCascadeService _overtimeCascadeService = null!;
    private IDayLockService _dayLockService = null!;
    private IWorkWriteGuard _workWriteGuard = null!;
    private IShiftDefaultExpensesApplier _defaultExpensesApplier = null!;
    private IClientPlanningPeriodGuard _planningPeriodGuard = null!;
    private IShiftVisibilityGuard _shiftVisibilityGuard = null!;
    private PeriodHoursResource _hoursA = null!;
    private PeriodHoursResource _hoursB = null!;
    private Guid _clientA;
    private Guid _clientB;
    private Guid _shift1;
    private Guid _shift2;

    [SetUp]
    public void SetUp()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _completionService = Substitute.For<IScheduleCompletionService>();
        _notificationFacade = Substitute.For<IWorkNotificationFacade>();
        _expansionService = Substitute.For<IContainerWorkExpansionService>();
        _overtimeCascadeService = Substitute.For<IOvertimeCascadeService>();
        _dayLockService = Substitute.For<IDayLockService>();
        _workWriteGuard = Substitute.For<IWorkWriteGuard>();
        _defaultExpensesApplier = Substitute.For<IShiftDefaultExpensesApplier>();
        _planningPeriodGuard = Substitute.For<IClientPlanningPeriodGuard>();
        _shiftVisibilityGuard = ShiftVisibilityGuards.AllowAll();
        _clientA = Guid.NewGuid();
        _clientB = Guid.NewGuid();
        _shift1 = Guid.NewGuid();
        _shift2 = Guid.NewGuid();
        _hoursA = new PeriodHoursResource { Hours = 8m };
        _hoursB = new PeriodHoursResource { Hours = 4m };

        _clientVisibilityGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _periodHoursService.GetPeriodBoundariesAsync(AprilDay).Returns((AprilStart, AprilEnd));
        _periodHoursService.GetPeriodBoundariesAsync(MayDay).Returns((MayStart, MayEnd));
        _periodHoursService.RecalculateAndNotifyAsync(
                Arg.Is(_clientA), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<string?>())
            .Returns(_hoursA);
        _periodHoursService.RecalculateAndNotifyAsync(
                Arg.Is(_clientB), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<string?>())
            .Returns(_hoursB);
        _notificationFacade.GetConnectionId().Returns(ConnectionId);
    }

    [Test]
    public async Task MixedClientsDatesAndTokens_RunsGuardsWritesAndNotificationsInOrder()
    {
        var token = Guid.NewGuid();

        await NewHandler().Handle(new BulkAddWorksCommand(NewMixedRequest(token)), CancellationToken.None);

        Received.InOrder(() =>
        {
            _clientVisibilityGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
            _shiftVisibilityGuard.FindFirstHiddenAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
            _dayLockService.EnsureNoneLockedAsync(
                Arg.Any<IReadOnlyCollection<(DateOnly Date, Guid ClientId, Guid? AnalyseToken)>>(), Arg.Any<CancellationToken>());
            _planningPeriodGuard.EnsureWithinPeriodsAsync(
                Arg.Any<IReadOnlyCollection<(Guid ClientId, DateOnly Date)>>(), Arg.Any<CancellationToken>());
            _workWriteGuard.FindHardBlockingRefusalAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>?>(),
                Arg.Any<WorkWriteOperation>(), Arg.Any<CancellationToken>());
            _workRepository.Add(Arg.Any<Work>());
            _workRepository.Add(Arg.Any<Work>());
            _workRepository.Add(Arg.Any<Work>());
            _expansionService.ExpandAsync(Arg.Any<Work>(), Arg.Any<DateOnly>());
            _expansionService.ExpandAsync(Arg.Any<Work>(), Arg.Any<DateOnly>());
            _expansionService.ExpandAsync(Arg.Any<Work>(), Arg.Any<DateOnly>());
            _defaultExpensesApplier.ApplyAsync(Arg.Any<IReadOnlyCollection<Work>>(), Arg.Any<CancellationToken>());
            _periodHoursService.GetPeriodBoundariesAsync(AprilDay);
            _periodHoursService.GetPeriodBoundariesAsync(MayDay);
            _completionService.SaveBulkAndTrackRangeAsync(
                Arg.Any<List<(Guid ClientId, DateOnly CurrentDate, Guid? AnalyseToken)>>(),
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>());
            _overtimeCascadeService.ReprocessSuccessorsAsync(Arg.Any<IReadOnlyCollection<Work>>());
            _notificationFacade.GetConnectionId();
            _notificationFacade.NotifyWorksBulkCreatedAsync(
                Arg.Any<IReadOnlyList<Work>>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<DateOnly, (DateOnly Start, DateOnly End)>>());
            _periodHoursService.RecalculateAndNotifyAsync(_clientA, AprilStart, MayEnd, null, ConnectionId);
            _periodHoursService.RecalculateAndNotifyAsync(_clientB, AprilStart, AprilEnd, token, ConnectionId);
            _notificationFacade.NotifyShiftStatsAsync(
                Arg.Any<HashSet<(Guid ShiftId, DateOnly Date)>>(), Arg.Any<string>(), Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task MixedClientsDatesAndTokens_SavesTheMergedRange_WithoutABulkToken()
    {
        var token = Guid.NewGuid();

        await NewHandler().Handle(new BulkAddWorksCommand(NewMixedRequest(token)), CancellationToken.None);

        var arguments = ArgumentsOf(_completionService, nameof(IScheduleCompletionService.SaveBulkAndTrackRangeAsync));
        var affected = (List<(Guid ClientId, DateOnly CurrentDate, Guid? AnalyseToken)>)arguments[0]!;
        affected.ShouldBe(new (Guid ClientId, DateOnly CurrentDate, Guid? AnalyseToken)[]
        {
            (_clientA, AprilDay, null),
            (_clientA, MayDay, null),
            (_clientB, AprilDay, token)
        });
        arguments[1].ShouldBe(AprilStart);
        arguments[2].ShouldBe(MayEnd);
        arguments[3].ShouldBeNull();
        await _periodHoursService.Received(1).GetPeriodBoundariesAsync(AprilDay);
        await _periodHoursService.Received(1).GetPeriodBoundariesAsync(MayDay);
    }

    [Test]
    public async Task MixedClientsDatesAndTokens_ReportsDeduplicatedShifts_AndOneBulkEvent()
    {
        var token = Guid.NewGuid();

        var response = await NewHandler().Handle(new BulkAddWorksCommand(NewMixedRequest(token)), CancellationToken.None);

        var statsArguments = _notificationFacade.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IWorkNotificationFacade.NotifyShiftStatsAsync))
            .GetArguments();
        var shifts = (HashSet<(Guid ShiftId, DateOnly Date)>)statsArguments[0]!;
        shifts.Count.ShouldBe(2);
        shifts.ShouldContain((_shift1, AprilDay));
        shifts.ShouldContain((_shift2, MayDay));
        statsArguments[1].ShouldBe(ConnectionId);
        statsArguments[2].ShouldBeNull();

        var bulkArguments = ArgumentsOf(_notificationFacade, nameof(IWorkNotificationFacade.NotifyWorksBulkCreatedAsync));
        var works = (IReadOnlyList<Work>)bulkArguments[0]!;
        works.Select(w => w.Id).ShouldBe(response.CreatedIds);
        bulkArguments[1].ShouldBe(ConnectionId);
        var boundaries = (IReadOnlyDictionary<DateOnly, (DateOnly Start, DateOnly End)>)bulkArguments[2]!;
        boundaries.Count.ShouldBe(2);
        boundaries[AprilDay].ShouldBe((AprilStart, AprilEnd));
        boundaries[MayDay].ShouldBe((MayStart, MayEnd));
    }

    [Test]
    public async Task MixedClientsDatesAndTokens_AnswersCreatedIdsPeriodHoursAndAffectedShifts()
    {
        var token = Guid.NewGuid();
        var request = NewMixedRequest(token);

        var response = await NewHandler().Handle(new BulkAddWorksCommand(request), CancellationToken.None);

        response.SuccessCount.ShouldBe(3);
        response.FailedCount.ShouldBe(0);
        response.CreatedIds.Count.ShouldBe(3);
        response.CreatedIds.Distinct().Count().ShouldBe(3);
        response.DeletedIds.ShouldBeEmpty();
        response.PeriodHours.ShouldNotBeNull();
        response.PeriodHours.Keys.ShouldBe(new[] { _clientA, _clientB });
        response.PeriodHours[_clientA].ShouldBeSameAs(_hoursA);
        response.PeriodHours[_clientB].ShouldBeSameAs(_hoursB);
        response.AffectedShifts.Count.ShouldBe(2);
        response.AffectedShifts.ShouldContain(p => p.ShiftId == _shift1 && p.Date == AprilDay.ToDateTime(TimeOnly.MinValue));
        response.AffectedShifts.ShouldContain(p => p.ShiftId == _shift2 && p.Date == MayDay.ToDateTime(TimeOnly.MinValue));

        var added = _workRepository.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IWorkRepository.Add))
            .Select(c => (Work)c.GetArguments()[0]!)
            .ToList();
        added.Select(w => w.Id).ShouldBe(response.CreatedIds);
        added.Select(w => w.ClientId).ShouldBe([_clientA, _clientA, _clientB]);
        added.Select(w => w.AnalyseToken).ShouldBe([null, null, token]);
        added[0].WorkTime.ShouldBe(request.Works[0].WorkTime);
        added[0].Surcharges.ShouldBe(request.Works[0].Surcharges);
        added[0].StartTime.ShouldBe(request.Works[0].StartTime);
        added[0].EndTime.ShouldBe(request.Works[0].EndTime);
        added[0].Information.ShouldBe(request.Works[0].Information);
        added[0].ShiftId.ShouldBe(_shift1);
        added[0].CurrentDate.ShouldBe(AprilDay);
    }

    [Test]
    public async Task OneTokenForTheWholeBatch_IsPassedAsBulkAndPerClientToken()
    {
        var token = Guid.NewGuid();
        var request = NewRequest(
            NewItem(_clientA, _shift1, AprilDay, token),
            NewItem(_clientB, _shift2, AprilDay, token));

        await NewHandler().Handle(new BulkAddWorksCommand(request), CancellationToken.None);

        await _completionService.Received(1).SaveBulkAndTrackRangeAsync(
            Arg.Any<List<(Guid ClientId, DateOnly CurrentDate, Guid? AnalyseToken)>>(), AprilStart, AprilEnd, token);
        await _periodHoursService.Received(1).RecalculateAndNotifyAsync(_clientA, AprilStart, AprilEnd, token, ConnectionId);
        await _periodHoursService.Received(1).RecalculateAndNotifyAsync(_clientB, AprilStart, AprilEnd, token, ConnectionId);
        await _notificationFacade.Received(1).NotifyShiftStatsAsync(
            Arg.Any<HashSet<(Guid ShiftId, DateOnly Date)>>(), ConnectionId, token, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnlyScenarioRows_DoNotAskTheHardBlockingGuard()
    {
        var token = Guid.NewGuid();

        await NewHandler().Handle(
            new BulkAddWorksCommand(NewRequest(NewItem(_clientA, _shift1, AprilDay, token))), CancellationToken.None);

        await _workWriteGuard.DidNotReceiveWithAnyArgs()
            .FindHardBlockingRefusalAsync(default(IReadOnlyList<PlannedWorkRow>)!, default, default, default);
        await _workRepository.Received(1).Add(Arg.Any<Work>());
    }

    [Test]
    public async Task RealRows_AreCheckedTogetherAsABulk_WithoutVacatedRows()
    {
        var token = Guid.NewGuid();

        await NewHandler().Handle(new BulkAddWorksCommand(NewMixedRequest(token)), CancellationToken.None);

        await _workWriteGuard.Received(1).FindHardBlockingRefusalAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows =>
                rows.Count == 2 && rows[0].ClientId == _clientA && rows[0].Date == AprilDay && rows[0].ShiftId == _shift1
                && rows[1].ClientId == _clientA && rows[1].Date == MayDay && rows[1].ShiftId == _shift2),
            Arg.Is<IReadOnlyList<PlannedRemovalRow>?>(vacated => vacated == null),
            Arg.Is(WorkWriteOperation.Bulk),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HardBlockingRefusal_RefusesTheWholeBatch_NothingWritten()
    {
        _workWriteGuard.FindHardBlockingRefusalAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>?>(),
                Arg.Any<WorkWriteOperation>(), Arg.Any<CancellationToken>())
            .Returns(new WorkBookingRefusal(RefusalCode, RefusalMessage, new Dictionary<string, object?>()));

        var ex = await Should.ThrowAsync<WorkWriteConflictException>(() => NewHandler().Handle(
            new BulkAddWorksCommand(NewMixedRequest(Guid.NewGuid())), CancellationToken.None));

        ex.Message.ShouldBe(RefusalMessage);
        ex.ErrorCode.ShouldBe(RefusalCode);
        await _workRepository.DidNotReceive().Add(Arg.Any<Work>());
        await _completionService.DidNotReceiveWithAnyArgs().SaveBulkAndTrackRangeAsync(default!, default, default, default);
        _notificationFacade.DidNotReceive().GetConnectionId();
    }

    [Test]
    public async Task EmptyRequest_WritesAndNotifiesNothing_AndAnswersAnEmptyResponse()
    {
        var response = await NewHandler().Handle(new BulkAddWorksCommand(NewRequest()), CancellationToken.None);

        response.SuccessCount.ShouldBe(0);
        response.FailedCount.ShouldBe(0);
        response.CreatedIds.ShouldBeEmpty();
        response.PeriodHours.ShouldBeNull();
        response.AffectedShifts.ShouldBeEmpty();
        await _workRepository.DidNotReceive().Add(Arg.Any<Work>());
        await _defaultExpensesApplier.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default);
        await _completionService.DidNotReceiveWithAnyArgs().SaveBulkAndTrackRangeAsync(default!, default, default, default);
        _notificationFacade.DidNotReceive().GetConnectionId();
        await _dayLockService.Received(1).EnsureNoneLockedAsync(
            Arg.Is<IReadOnlyCollection<(DateOnly Date, Guid ClientId, Guid? AnalyseToken)>>(entries => entries.Count == 0),
            Arg.Any<CancellationToken>());
    }

    private static object?[] ArgumentsOf(object substitute, string methodName)
        => substitute.ReceivedCalls().Single(c => c.GetMethodInfo().Name == methodName).GetArguments();

    private BulkAddWorksRequest NewMixedRequest(Guid token)
        => NewRequest(
            NewItem(_clientA, _shift1, AprilDay, null),
            NewItem(_clientA, _shift2, MayDay, null),
            NewItem(_clientB, _shift1, AprilDay, token));

    private static BulkAddWorksRequest NewRequest(params BulkWorkItem[] items) => new()
    {
        PeriodStart = AprilStart,
        PeriodEnd = MayEnd,
        Works = items.ToList()
    };

    private static BulkWorkItem NewItem(Guid clientId, Guid shiftId, DateOnly day, Guid? token) => new()
    {
        ClientId = clientId,
        ShiftId = shiftId,
        CurrentDate = day,
        WorkTime = 8.5m,
        Surcharges = 1.25m,
        StartTime = new TimeOnly(7, 0),
        EndTime = new TimeOnly(15, 30),
        Information = "info",
        AnalyseToken = token
    };

    private BulkAddWorksCommandHandler NewHandler() => new(
        _workRepository, _clientVisibilityGuard, new ScheduleMapper(), _periodHoursService,
        _completionService, _notificationFacade, _expansionService,
        _overtimeCascadeService, _dayLockService, _workWriteGuard,
        _defaultExpensesApplier, _planningPeriodGuard, _shiftVisibilityGuard,
        Substitute.For<ILogger<BulkAddWorksCommandHandler>>());
}
