// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ThoroughRecalculationBackgroundService: sealed works (LockLevel != None) and work changes
/// on sealed works must never be pushed through the macro pipeline (their persisted surcharges stay
/// unchanged), a client-scoped request leaves works of unscoped clients untouched and pulls period
/// hours for exactly the scoped clients, explicit client ids combined with a selected group narrow
/// the scope to their intersection (an empty intersection skips all reprocessing but still sends the
/// completion notification), and identical pending queue requests (including the same client set in
/// a different order) are coalesced instead of piling up.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Domain.Interfaces.Schedules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Services;

[TestFixture]
public class ThoroughRecalculationBackgroundServiceTests
{
    private const decimal MutatedSurcharge = 99m;
    private const decimal OriginalSurcharge = 5m;
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

    private DataBaseContext _context = null!;
    private IWorkMacroService _workMacroService = null!;
    private IBreakMacroService _breakMacroService = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private IWorkNotificationService _notificationService = null!;
    private IGetAllClientIdsFromGroupAndSubgroups _groupClient = null!;
    private ServiceProvider _serviceProvider = null!;
    private ThoroughRecalculationBackgroundService _sut = null!;

    private readonly DateOnly _startDate = new(2026, 6, 1);
    private readonly DateOnly _endDate = new(2026, 6, 30);

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);

        _workMacroService = Substitute.For<IWorkMacroService>();
        _workMacroService.ProcessWorkMacroAsync(Arg.Any<Work>())
            .Returns(Task.CompletedTask)
            .AndDoes(callInfo => callInfo.Arg<Work>().Surcharges = MutatedSurcharge);

        _breakMacroService = Substitute.For<IBreakMacroService>();
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _notificationService = Substitute.For<IWorkNotificationService>();
        _groupClient = Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>();

        var services = new ServiceCollection();
        services.AddSingleton(_context);
        services.AddSingleton(_workMacroService);
        services.AddSingleton(_breakMacroService);
        services.AddSingleton(_periodHoursService);
        services.AddSingleton(_notificationService);
        services.AddSingleton(_groupClient);
        _serviceProvider = services.BuildServiceProvider();

        _sut = new ThoroughRecalculationBackgroundService(
            _serviceProvider,
            NullLogger<ThoroughRecalculationBackgroundService>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _sut.StopAsync(CancellationToken.None);
        _sut.Dispose();
        await _serviceProvider.DisposeAsync();
        _context.Dispose();
    }

    [Test]
    public async Task ProcessRequest_SealedWork_IsSkippedAndItsSurchargesStayUnchanged()
    {
        var clientId = Guid.NewGuid();
        var sealedWork = AddWork(clientId, new DateOnly(2026, 6, 10), WorkLockLevel.Closed);
        var openWork = AddWork(clientId, new DateOnly(2026, 6, 11), WorkLockLevel.None);
        await _context.SaveChangesAsync();

        _sut.QueueRecalculation(_startDate, _endDate, null, null);
        await RunUntilCompletionNotificationAsync();

        await _workMacroService.DidNotReceive().ProcessWorkMacroAsync(Arg.Is<Work>(w => w.Id == sealedWork.Id));
        await _workMacroService.Received(1).ProcessWorkMacroAsync(Arg.Is<Work>(w => w.Id == openWork.Id));

        var persistedSealed = await _context.Work.SingleAsync(w => w.Id == sealedWork.Id);
        persistedSealed.Surcharges.ShouldBe(OriginalSurcharge);

        var persistedOpen = await _context.Work.SingleAsync(w => w.Id == openWork.Id);
        persistedOpen.Surcharges.ShouldBe(MutatedSurcharge);
    }

    [Test]
    public async Task ProcessRequest_WorkChangeOnSealedWork_IsNotReprocessed()
    {
        var clientId = Guid.NewGuid();
        var sealedWork = AddWork(clientId, new DateOnly(2026, 6, 10), WorkLockLevel.Approved);
        var openWork = AddWork(clientId, new DateOnly(2026, 6, 11), WorkLockLevel.None);
        var sealedWorkChange = AddWorkChange(sealedWork);
        var openWorkChange = AddWorkChange(openWork);
        await _context.SaveChangesAsync();

        _sut.QueueRecalculation(_startDate, _endDate, null, null);
        await RunUntilCompletionNotificationAsync();

        await _workMacroService.DidNotReceive().ProcessWorkChangeMacroAsync(Arg.Is<WorkChange>(wc => wc.Id == sealedWorkChange.Id));
        await _workMacroService.Received(1).ProcessWorkChangeMacroAsync(Arg.Is<WorkChange>(wc => wc.Id == openWorkChange.Id));
    }

    [Test]
    public void QueueRecalculation_IdenticalPendingRequest_IsCoalesced()
    {
        var first = _sut.QueueRecalculation(_startDate, _endDate, null, null);
        var second = _sut.QueueRecalculation(_startDate, _endDate, null, null);
        var different = _sut.QueueRecalculation(_startDate, _endDate.AddDays(1), null, null);

        first.ShouldBeTrue();
        second.ShouldBeTrue();
        different.ShouldBeTrue();
        _sut.PendingRequestCount.ShouldBe(2);
    }

    [Test]
    public void QueueRecalculation_SameClientSetInDifferentOrder_IsCoalesced()
    {
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();

        var first = _sut.QueueRecalculation(_startDate, _endDate, null, null, new[] { clientA, clientB });
        var second = _sut.QueueRecalculation(_startDate, _endDate, null, null, new[] { clientB, clientA });

        first.ShouldBeTrue();
        second.ShouldBeTrue();
        _sut.PendingRequestCount.ShouldBe(1);
    }

    [Test]
    public void QueueRecalculation_DifferentClientSet_IsNotCoalesced()
    {
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();

        _sut.QueueRecalculation(_startDate, _endDate, null, null, new[] { clientA, clientB }).ShouldBeTrue();
        _sut.QueueRecalculation(_startDate, _endDate, null, null, new[] { clientA }).ShouldBeTrue();
        _sut.QueueRecalculation(_startDate, _endDate, null, null).ShouldBeTrue();

        _sut.PendingRequestCount.ShouldBe(3);
    }

    [Test]
    public async Task ProcessRequest_ClientScopedRequest_LeavesWorksOfUnscopedClientsUntouched()
    {
        var scopedClient = Guid.NewGuid();
        var unscopedClient = Guid.NewGuid();
        var scopedWork = AddWork(scopedClient, new DateOnly(2026, 6, 10), WorkLockLevel.None);
        var unscopedWork = AddWork(unscopedClient, new DateOnly(2026, 6, 11), WorkLockLevel.None);
        await _context.SaveChangesAsync();

        _sut.QueueRecalculation(_startDate, _endDate, null, null, new[] { scopedClient });
        await RunUntilCompletionNotificationAsync();

        await _workMacroService.Received(1).ProcessWorkMacroAsync(Arg.Is<Work>(w => w.Id == scopedWork.Id));
        await _workMacroService.DidNotReceive().ProcessWorkMacroAsync(Arg.Is<Work>(w => w.Id == unscopedWork.Id));

        var persistedUnscoped = await _context.Work.SingleAsync(w => w.Id == unscopedWork.Id);
        persistedUnscoped.Surcharges.ShouldBe(OriginalSurcharge);

        await _breakMacroService.Received(1).ReprocessAllBreaksAsync(
            _startDate, _endDate,
            Arg.Is<List<Guid>>(ids => ids.Count == 1 && ids.Contains(scopedClient)));
    }

    [Test]
    public async Task ProcessRequest_ClientScopedRequest_PullsPeriodHoursForExactlyTheScopedClients()
    {
        var scopedClient = Guid.NewGuid();
        AddWork(scopedClient, new DateOnly(2026, 6, 10), WorkLockLevel.None);
        AddWork(Guid.NewGuid(), new DateOnly(2026, 6, 11), WorkLockLevel.None);
        await _context.SaveChangesAsync();

        _sut.QueueRecalculation(_startDate, _endDate, null, null, new[] { scopedClient });
        await RunUntilCompletionNotificationAsync();

        await _periodHoursService.Received(1).RecalculatePeriodHoursAsync(scopedClient, _startDate, _endDate, null);
        await _periodHoursService.DidNotReceiveWithAnyArgs().RecalculateAllClientsAsync(default, default, null, null);
    }

    [Test]
    public async Task ProcessRequest_ExplicitClientIdsAndSelectedGroup_ReprocessesOnlyTheIntersection()
    {
        var groupId = Guid.NewGuid();
        var groupOnlyClient = Guid.NewGuid();
        var intersectionClient = Guid.NewGuid();
        var explicitOnlyClient = Guid.NewGuid();
        _groupClient.GetAllClientIdsFromGroupAndSubgroups(groupId)
            .Returns(new List<Guid> { groupOnlyClient, intersectionClient });

        var groupOnlyWork = AddWork(groupOnlyClient, new DateOnly(2026, 6, 10), WorkLockLevel.None);
        var intersectionWork = AddWork(intersectionClient, new DateOnly(2026, 6, 11), WorkLockLevel.None);
        var explicitOnlyWork = AddWork(explicitOnlyClient, new DateOnly(2026, 6, 12), WorkLockLevel.None);
        await _context.SaveChangesAsync();

        _sut.QueueRecalculation(_startDate, _endDate, groupId, null, new[] { intersectionClient, explicitOnlyClient });
        await RunUntilCompletionNotificationAsync();

        await _workMacroService.Received(1).ProcessWorkMacroAsync(Arg.Is<Work>(w => w.Id == intersectionWork.Id));
        await _workMacroService.DidNotReceive().ProcessWorkMacroAsync(Arg.Is<Work>(w => w.Id == groupOnlyWork.Id));
        await _workMacroService.DidNotReceive().ProcessWorkMacroAsync(Arg.Is<Work>(w => w.Id == explicitOnlyWork.Id));

        await _periodHoursService.Received(1).RecalculatePeriodHoursAsync(intersectionClient, _startDate, _endDate, null);
        await _periodHoursService.DidNotReceive().RecalculatePeriodHoursAsync(groupOnlyClient, _startDate, _endDate, null);
        await _periodHoursService.DidNotReceive().RecalculatePeriodHoursAsync(explicitOnlyClient, _startDate, _endDate, null);
        await _periodHoursService.DidNotReceiveWithAnyArgs().RecalculateAllClientsAsync(default, default, null, null);
    }

    [Test]
    public async Task ProcessRequest_ScopeResolvesToNoClients_SkipsAllReprocessingButStillSendsCompletion()
    {
        var groupId = Guid.NewGuid();
        var groupClient = Guid.NewGuid();
        var explicitClient = Guid.NewGuid();
        _groupClient.GetAllClientIdsFromGroupAndSubgroups(groupId)
            .Returns(new List<Guid> { groupClient });

        AddWork(groupClient, new DateOnly(2026, 6, 10), WorkLockLevel.None);
        AddWork(explicitClient, new DateOnly(2026, 6, 11), WorkLockLevel.None);
        await _context.SaveChangesAsync();

        _sut.QueueRecalculation(_startDate, _endDate, groupId, null, new[] { explicitClient });
        await RunUntilCompletionNotificationAsync();

        await _workMacroService.DidNotReceiveWithAnyArgs().ProcessWorkMacroAsync(Arg.Any<Work>());
        await _workMacroService.DidNotReceiveWithAnyArgs().ProcessWorkChangeMacroAsync(Arg.Any<WorkChange>());
        await _breakMacroService.DidNotReceiveWithAnyArgs().ReprocessAllBreaksAsync(default, default, null);
        await _periodHoursService.DidNotReceiveWithAnyArgs().RecalculatePeriodHoursAsync(default, default, default, null);
        await _periodHoursService.DidNotReceiveWithAnyArgs().RecalculateAllClientsAsync(default, default, null, null);

        await _notificationService.Received(1).NotifyThoroughRecalculationCompleted(
            Arg.Is<ThoroughRecalculationCompletedDto>(dto =>
                dto.ProcessedWorks == 0 && dto.ProcessedWorkChanges == 0 && dto.ProcessedBreaks == 0));
    }

    [Test]
    public async Task ProcessRequest_UnscopedRequest_StillRecalculatesAllClients()
    {
        AddWork(Guid.NewGuid(), new DateOnly(2026, 6, 10), WorkLockLevel.None);
        await _context.SaveChangesAsync();

        _sut.QueueRecalculation(_startDate, _endDate, null, null);
        await RunUntilCompletionNotificationAsync();

        await _periodHoursService.Received(1).RecalculateAllClientsAsync(_startDate, _endDate, null, null);
        await _periodHoursService.DidNotReceiveWithAnyArgs().RecalculatePeriodHoursAsync(default, default, default, null);
    }

    [Test]
    public void QueueRecalculation_QueueIsFull_ReturnsFalseInsteadOfDroppingAnOlderRequest()
    {
        var capacity = ThoroughRecalculationBackgroundService.ChannelCapacity;

        for (var i = 0; i < capacity; i++)
        {
            _sut.QueueRecalculation(_startDate.AddDays(i), _endDate, null, null).ShouldBeTrue();
        }

        var overflow = _sut.QueueRecalculation(_startDate.AddDays(capacity), _endDate, null, null);

        overflow.ShouldBeFalse();
        _sut.PendingRequestCount.ShouldBe(capacity);
    }

    [Test]
    public async Task ProcessAllUnsealed_FindsItsOwnSpan_AndWorksMonthByMonth()
    {
        var clientId = Guid.NewGuid();
        var earliest = AddWork(clientId, new DateOnly(2024, 1, 15), WorkLockLevel.None);
        var middle = AddWork(clientId, new DateOnly(2025, 7, 3), WorkLockLevel.None);
        var latest = AddWork(clientId, new DateOnly(2026, 6, 10), WorkLockLevel.None);
        AddWork(clientId, new DateOnly(2020, 1, 1), WorkLockLevel.Closed);
        AddWork(clientId, new DateOnly(2030, 1, 1), WorkLockLevel.Approved);
        await _context.SaveChangesAsync();

        _sut.QueueRecalculationOfAllUnsealed().ShouldBeTrue();
        await RunUntilCompletionNotificationAsync();

        foreach (var work in new[] { earliest, middle, latest })
        {
            await _workMacroService.Received(1).ProcessWorkMacroAsync(Arg.Is<Work>(w => w.Id == work.Id));
        }

        await _workMacroService.Received(3).ProcessWorkMacroAsync(Arg.Any<Work>());
        await _periodHoursService.Received(1).RecalculateAllClientsAsync(
            new DateOnly(2024, 1, 15), new DateOnly(2024, 1, 31), null, null);
        await _periodHoursService.Received(1).RecalculateAllClientsAsync(
            new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 29), null, null);
        await _periodHoursService.Received(1).RecalculateAllClientsAsync(
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 10), null, null);
        await _periodHoursService.Received(30).RecalculateAllClientsAsync(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), null, null);
        await _notificationService.Received(1).NotifyThoroughRecalculationCompleted(
            Arg.Is<ThoroughRecalculationCompletedDto>(dto =>
                dto.StartDate == new DateOnly(2024, 1, 15)
                && dto.EndDate == new DateOnly(2026, 6, 10)
                && dto.ProcessedWorks == 3
                && dto.SelectedGroup == null
                && dto.AnalyseToken == null));
    }

    [Test]
    public async Task ProcessAllUnsealed_SealedWorksAndScenarioWorksAndDeletedWorks_AreNotProcessed()
    {
        var clientId = Guid.NewGuid();
        var open = AddWork(clientId, new DateOnly(2026, 6, 10), WorkLockLevel.None);
        var sealedWork = AddWork(clientId, new DateOnly(2026, 6, 11), WorkLockLevel.Closed);
        var scenario = AddWork(clientId, new DateOnly(2026, 6, 12), WorkLockLevel.None);
        scenario.AnalyseToken = Guid.NewGuid();
        var deleted = AddWork(clientId, new DateOnly(2026, 6, 13), WorkLockLevel.None);
        deleted.IsDeleted = true;
        await _context.SaveChangesAsync();

        _sut.QueueRecalculationOfAllUnsealed();
        await RunUntilCompletionNotificationAsync();

        await _workMacroService.Received(1).ProcessWorkMacroAsync(Arg.Is<Work>(w => w.Id == open.Id));
        await _workMacroService.DidNotReceive().ProcessWorkMacroAsync(
            Arg.Is<Work>(w => w.Id == sealedWork.Id || w.Id == scenario.Id || w.Id == deleted.Id));
        (await _context.Work.SingleAsync(w => w.Id == sealedWork.Id)).Surcharges.ShouldBe(OriginalSurcharge);
    }

    [Test]
    public async Task ProcessAllUnsealed_AnUnsealedBreakOutsideTheWorkSpan_ExtendsTheSpan_ASealedOneDoesNot()
    {
        var clientId = Guid.NewGuid();
        AddWork(clientId, new DateOnly(2026, 6, 10), WorkLockLevel.None);
        AddBreak(clientId, new DateOnly(2026, 3, 5), WorkLockLevel.None);
        AddBreak(clientId, new DateOnly(2019, 1, 1), WorkLockLevel.Closed);
        await _context.SaveChangesAsync();

        _sut.QueueRecalculationOfAllUnsealed();
        await RunUntilCompletionNotificationAsync();

        await _breakMacroService.Received(1).ReprocessAllBreaksAsync(
            new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 31), Arg.Any<List<Guid>?>());
        await _breakMacroService.DidNotReceive().ReprocessAllBreaksAsync(
            new DateOnly(2019, 1, 1), Arg.Any<DateOnly>(), Arg.Any<List<Guid>?>());
        await _notificationService.Received(1).NotifyThoroughRecalculationCompleted(
            Arg.Is<ThoroughRecalculationCompletedDto>(dto =>
                dto.StartDate == new DateOnly(2026, 3, 5) && dto.EndDate == new DateOnly(2026, 6, 10)));
    }

    [Test]
    public async Task ProcessAllUnsealed_NothingUnsealed_SendsOneCompletionAndProcessesNothing()
    {
        AddWork(Guid.NewGuid(), new DateOnly(2026, 6, 10), WorkLockLevel.Closed);
        await _context.SaveChangesAsync();

        _sut.QueueRecalculationOfAllUnsealed();
        await RunUntilCompletionNotificationAsync();

        await _workMacroService.DidNotReceiveWithAnyArgs().ProcessWorkMacroAsync(default!);
        await _periodHoursService.DidNotReceiveWithAnyArgs().RecalculateAllClientsAsync(default, default, null, null);
        await _notificationService.Received(1).NotifyThoroughRecalculationCompleted(
            Arg.Is<ThoroughRecalculationCompletedDto>(dto => dto.ProcessedWorks == 0 && dto.ProcessedBreaks == 0));
    }

    [Test]
    public void QueueRecalculationOfAllUnsealed_IdenticalPendingRequest_IsCoalesced_AndDiffersFromAWindowRequest()
    {
        _sut.QueueRecalculationOfAllUnsealed().ShouldBeTrue();
        _sut.QueueRecalculationOfAllUnsealed().ShouldBeTrue();
        _sut.QueueRecalculation(DateOnly.MinValue, DateOnly.MaxValue, null, null).ShouldBeTrue();

        _sut.PendingRequestCount.ShouldBe(2);
    }

    private Work AddWork(Guid clientId, DateOnly date, WorkLockLevel lockLevel)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = Guid.NewGuid(),
            CurrentDate = date,
            WorkTime = 8m,
            Surcharges = OriginalSurcharge,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            LockLevel = lockLevel,
        };
        _context.Work.Add(work);
        return work;
    }

    private Break AddBreak(Guid clientId, DateOnly date, WorkLockLevel lockLevel)
    {
        var breakEntry = new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AbsenceId = Guid.NewGuid(),
            CurrentDate = date,
            LockLevel = lockLevel,
        };
        _context.Break.Add(breakEntry);
        return breakEntry;
    }

    private WorkChange AddWorkChange(Work work)
    {
        var workChange = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Work = work,
        };
        _context.WorkChange.Add(workChange);
        return workChange;
    }

    private async Task RunUntilCompletionNotificationAsync()
    {
        var completed = new TaskCompletionSource();
        _notificationService
            .NotifyThoroughRecalculationCompleted(Arg.Any<ThoroughRecalculationCompletedDto>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => completed.TrySetResult());

        await _sut.StartAsync(CancellationToken.None);

        var finished = await Task.WhenAny(completed.Task, Task.Delay(CompletionTimeout));
        finished.ShouldBe(completed.Task, "thorough recalculation did not complete within the timeout");
    }
}
