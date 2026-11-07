// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization of the commit chain of the Work create handler as it runs today: the created Work instance flows
/// through add, container expansion, default expenses, the first commit, the overtime successors, the completion
/// service and the two handler notifications in that order. A failure in the successors or in the completion service
/// comes after the first commit, skips every handler notification and the three-day read, and is answered by
/// BaseHandler's catch-all as an InvalidRequestException carrying the inner message. These tests pin observed
/// behaviour so that a later change to the post-commit steps flips them deliberately.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Domain.Exceptions;
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
    private const string CreateOperationName = "CreateWork";
    private const string ConnectionId = "connection-1";
    private const string InjectedFailureMessage = "injected post-commit failure";
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
    private IOvertimeCascadeService _overtimeCascadeService = null!;

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
        _overtimeCascadeService = Substitute.For<IOvertimeCascadeService>();

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
            .SaveAndTrackAsync(_clientId, Day, PeriodStart, PeriodEnd, _analyseToken)
            .Returns(new PeriodHoursResource { Hours = PeriodHours });
        ScheduleEntriesStub.Serve(_scheduleEntriesService, []);
    }

    [TearDown]
    public void TearDown() => _cancellation.Dispose();

    [Test]
    public async Task Post_RunsAddExpandExpensesCommitReprocessTrackAndNotifications_InThatOrder()
    {
        await NewPostHandler().Handle(new PostCommand<WorkResource>(NewResource()), _cancellation.Token);

        var work = _created.ShouldNotBeNull();
        work.Id.ShouldBe(_workId);
        Received.InOrder(() =>
        {
            _periodHoursService.GetPeriodBoundariesAsync(Day);
            _workRepository.Add(work);
            _expansionService.ExpandAsync(work, Day);
            _defaultExpensesApplier.ApplyAsync(
                Arg.Is<IReadOnlyCollection<Work>>(works => works.Count == 1 && ReferenceEquals(works.First(), work)),
                _cancellation.Token);
            _unitOfWork.CompleteAsync();
            _overtimeCascadeService.ReprocessSuccessorsAsync(work);
            _completionService.SaveAndTrackAsync(_clientId, Day, PeriodStart, PeriodEnd, _analyseToken);
            _notificationFacade.NotifyWorkCreatedAsync(work, ConnectionId, PeriodStart, PeriodEnd);
            _notificationFacade.NotifyShiftStatsAsync(_shiftId, Day, ConnectionId, _analyseToken, _cancellation.Token);
        });
    }

    [Test]
    public async Task Post_WhenReprocessThrowsAPlainException_HasAlreadyCommittedOnce_SkipsTrackAndNotifications_AndAnswersTheCatchAllInvalidRequest()
    {
        _overtimeCascadeService
            .ReprocessSuccessorsAsync(Arg.Is<Work>(work => work.Id == _workId))
            .Returns<Task>(_ => throw new Exception(InjectedFailureMessage));

        var exception = await Should.ThrowAsync<InvalidRequestException>(
            () => NewPostHandler().Handle(new PostCommand<WorkResource>(NewResource()), _cancellation.Token));

        exception.Message.ShouldBe(CatchAllMessage());
        await _unitOfWork.Received(1).CompleteAsync();
        await _completionService.DidNotReceiveWithAnyArgs().SaveAndTrackAsync(default, default, default, default, default);
        await ThenNoHandlerNotificationWasSentAndNothingWasReadBackAsync();
    }

    [Test]
    public async Task Post_WhenSaveAndTrackThrowsAPlainException_HasCommittedAndReprocessed_SkipsHandlerNotifications_AndAnswersTheCatchAllInvalidRequest()
    {
        _completionService
            .SaveAndTrackAsync(_clientId, Day, PeriodStart, PeriodEnd, _analyseToken)
            .Returns<Task<PeriodHoursResource>>(_ => throw new Exception(InjectedFailureMessage));

        var exception = await Should.ThrowAsync<InvalidRequestException>(
            () => NewPostHandler().Handle(new PostCommand<WorkResource>(NewResource()), _cancellation.Token));

        exception.Message.ShouldBe(CatchAllMessage());
        var work = _created.ShouldNotBeNull();
        await _unitOfWork.Received(1).CompleteAsync();
        await _overtimeCascadeService.Received(1).ReprocessSuccessorsAsync(work);
        await _completionService.Received(1).SaveAndTrackAsync(_clientId, Day, PeriodStart, PeriodEnd, _analyseToken);
        await ThenNoHandlerNotificationWasSentAndNothingWasReadBackAsync();
    }

    private async Task ThenNoHandlerNotificationWasSentAndNothingWasReadBackAsync()
    {
        await _notificationFacade.DidNotReceiveWithAnyArgs().NotifyWorkCreatedAsync(default!, default!, default, default);
        await _notificationFacade.DidNotReceiveWithAnyArgs().NotifyPeriodHoursUpdatedAsync(
            default, default, default, default!, default!, default);
        await _notificationFacade.DidNotReceiveWithAnyArgs().NotifyShiftStatsAsync(
            default, default, default!, default, default);
        await _notificationFacade.DidNotReceiveWithAnyArgs().NotifyShiftStatsAsync(
            default(HashSet<(Guid ShiftId, DateOnly Date)>)!, default!, default, default);
        await _scheduleEntriesService.DidNotReceiveWithAnyArgs().GetScheduleEntriesAsync(default!, default);
    }

    private static string CatchAllMessage()
        => $"An unexpected error occurred during {CreateOperationName}: {InjectedFailureMessage}";

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
            _overtimeCascadeService, Substitute.For<IWorkWriteGuard>(), Substitute.For<IClientPlanningPeriodGuard>(),
            ShiftVisibilityGuards.AllowAll(), Substitute.For<ILogger<PostCommandHandler>>());
}
