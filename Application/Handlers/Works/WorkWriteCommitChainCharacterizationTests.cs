// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Commit chain of the Work create handler: the created Work instance flows through add, container expansion, default
/// expenses, the commit, the completion service (overtime successors and period hours,
/// IScheduleCompletionService.CompleteWorkWriteAsync) and the two handler notifications in that order. A failure after
/// the commit is handled inside the completion service (ScheduleCompletionServiceWorkWriteTests); when it answers
/// without period hours, the handler still notifies, reads the three-day window and returns the created Work.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class WorkWriteCommitChainCharacterizationTests
{
    private const string ConnectionId = "connection-1";
    private const decimal PeriodHours = 8m;

    private static readonly DateOnly Day = new(2027, 5, 12);
    private static readonly DateOnly PeriodStart = new(2027, 5, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 5, 31);

    private IWorkRepository _workRepository = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private IScheduleEntriesService _scheduleEntriesService = null!;
    private IScheduleCompletionService _completionService = null!;
    private IWorkNotificationFacade _notificationFacade = null!;
    private IShiftDefaultExpensesApplier _defaultExpensesApplier = null!;
    private IContainerWorkExpansionService _expansionService = null!;
    private ISelectedGroupContextResolver _groupContextResolver = null!;
    private IUnitOfWork _unitOfWork = null!;

    private CancellationTokenSource _cancellation = null!;
    private Guid _analyseToken;
    private Guid _clientId;
    private Guid _shiftId;
    private Guid _workId;
    private Work? _created;

    [SetUp]
    public void SetUp()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _scheduleEntriesService = Substitute.For<IScheduleEntriesService>();
        _completionService = Substitute.For<IScheduleCompletionService>();
        _notificationFacade = Substitute.For<IWorkNotificationFacade>();
        _defaultExpensesApplier = Substitute.For<IShiftDefaultExpensesApplier>();
        _expansionService = Substitute.For<IContainerWorkExpansionService>();
        _groupContextResolver = Substitute.For<ISelectedGroupContextResolver>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _cancellation = new CancellationTokenSource();
        _analyseToken = Guid.NewGuid();
        _clientId = Guid.NewGuid();
        _shiftId = Guid.NewGuid();
        _workId = Guid.NewGuid();
        _created = null;

        _workRepository.Add(Arg.Do<Work>(work => _created = work)).Returns(Task.CompletedTask);
        _groupContextResolver.ResolveVisibleGroupIdsAsync().Returns(new List<Guid>());
        _periodHoursService.GetPeriodBoundariesAsync(Day).Returns((PeriodStart, PeriodEnd));
        _notificationFacade.GetConnectionId().Returns(ConnectionId);
        _completionService
            .CompleteWorkWriteAsync(Arg.Is<Work>(work => work.Id == _workId), Arg.Is<Work?>(previous => previous == null), PeriodStart, PeriodEnd)
            .Returns(new WorkWriteCompletion(new PeriodHoursResource { Hours = PeriodHours }, null));
        ScheduleEntriesStub.Serve(_scheduleEntriesService, []);
    }

    [TearDown]
    public void TearDown() => _cancellation.Dispose();

    [Test]
    public async Task Post_RunsAddExpandExpensesCommitCompletionAndNotifications_InThatOrder()
    {
        var result = await NewPostHandler().Handle(new PostCommand<WorkResource>(NewResource()), _cancellation.Token);

        var work = _created.ShouldNotBeNull();
        work.Id.ShouldBe(_workId);
        result.ShouldNotBeNull().PeriodHours.ShouldNotBeNull().Hours.ShouldBe(PeriodHours);
        Received.InOrder(() =>
        {
            _periodHoursService.GetPeriodBoundariesAsync(Day);
            _workRepository.Add(work);
            _expansionService.ExpandAsync(work, Day);
            _defaultExpensesApplier.ApplyAsync(
                Arg.Is<IReadOnlyCollection<Work>>(works => works.Count == 1 && ReferenceEquals(works.First(), work)),
                _cancellation.Token);
            _unitOfWork.CompleteAsync();
            _completionService.CompleteWorkWriteAsync(work, null, PeriodStart, PeriodEnd);
            _notificationFacade.NotifyWorkCreatedAsync(work, ConnectionId, PeriodStart, PeriodEnd);
            _notificationFacade.NotifyShiftStatsAsync(_shiftId, Day, ConnectionId, _analyseToken, _cancellation.Token);
        });
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Post_WhenTheCompletionAnswersWithoutPeriodHours_StillNotifiesReadsBackAndReturnsTheCreatedWork()
    {
        _completionService
            .CompleteWorkWriteAsync(Arg.Is<Work>(work => work.Id == _workId), Arg.Is<Work?>(previous => previous == null), PeriodStart, PeriodEnd)
            .Returns(new WorkWriteCompletion(null, null));

        var result = await NewPostHandler().Handle(new PostCommand<WorkResource>(NewResource()), _cancellation.Token);

        var work = _created.ShouldNotBeNull();
        result.ShouldNotBeNull().Id.ShouldBe(_workId);
        result.PeriodHours.ShouldBeNull();
        await _notificationFacade.Received(1).NotifyWorkCreatedAsync(work, ConnectionId, PeriodStart, PeriodEnd);
        await _notificationFacade.Received(1).NotifyShiftStatsAsync(
            _shiftId, Day, ConnectionId, _analyseToken, _cancellation.Token);
        await _notificationFacade.DidNotReceiveWithAnyArgs().NotifyPeriodHoursUpdatedAsync(
            default, default, default, default!, default!, default);
        await _scheduleEntriesService.Received(1).GetScheduleEntriesAsync(
            Arg.Is<ScheduleEntriesCriteria>(criteria => criteria.AnalyseToken == _analyseToken), _cancellation.Token);
    }

    private WorkResource NewResource()
        => new()
        {
            Id = _workId,
            ClientId = _clientId,
            ShiftId = _shiftId,
            CurrentDate = Day,
            AnalyseToken = _analyseToken
        };

    private PostCommandHandler NewPostHandler()
        => new(
            _workRepository, TestGroupWriteVisibility.AllClientsVisible(), new ScheduleMapper(), _periodHoursService,
            _scheduleEntriesService, _completionService, _notificationFacade, _defaultExpensesApplier,
            _expansionService, _groupContextResolver, Substitute.For<IDayLockService>(), _unitOfWork,
            Substitute.For<IWorkWriteGuard>(), Substitute.For<IClientPlanningPeriodGuard>(),
            ShiftVisibilityGuards.AllowAll(), Substitute.For<ILogger<PostCommandHandler>>());
}
