// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ScheduleCompletionService.CompleteWorkWriteAsync completes a single-Work write the caller has already committed. On
/// success it queues the timeline check, reprocesses the overtime successors, tracks the change and recalculates and
/// pushes the period hours, without touching the repair path; without a push it also stores the period a move left and
/// the following period a week past the day reaches (since 2026-11-07). When the cascade or the recalculation throws, the write
/// does not fail: the tracked changes are discarded, the pay period is queued for a thorough recalculation (the following
/// period first when the Work's day plus a week passes the period end), the cached rows of the affected days are dropped
/// and the live period hours are returned; no repair step lets its own failure escape.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class ScheduleCompletionServiceWorkWriteTests
{
    private const string ConnectionId = "connection-1";
    private const string InjectedFailureMessage = "injected post-commit failure";
    private const decimal RecalculatedHours = 8m;
    private const decimal PreviousRecalculatedHours = 3m;
    private const decimal LiveHours = 5m;
    private const decimal PreviousLiveHours = 2m;

    private static readonly DateOnly Day = new(2027, 3, 10);
    private static readonly DateOnly PeriodStart = new(2027, 3, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 3, 31);
    private static readonly DateOnly LastDayWithoutLookAhead = new(2027, 3, 24);
    private static readonly DateOnly FirstDayWithLookAhead = new(2027, 3, 25);
    private static readonly DateOnly FollowingStart = new(2027, 4, 1);
    private static readonly DateOnly FollowingEnd = new(2027, 4, 30);

    private IUnitOfWork _unitOfWork = null!;
    private IScheduleChangeTracker _changeTracker = null!;
    private IScheduleTimelineService _timeline = null!;
    private IPeriodHoursService _periodHours = null!;
    private IOvertimeCascadeService _cascade = null!;
    private IThoroughRecalculationQueue _queue = null!;
    private ILogger<ScheduleCompletionService> _logger = null!;
    private ScheduleCompletionService _service = null!;

    private Guid _clientId;
    private Guid _previousClientId;
    private Guid _analyseToken;

    [SetUp]
    public void SetUp()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _changeTracker = Substitute.For<IScheduleChangeTracker>();
        _timeline = Substitute.For<IScheduleTimelineService>();
        _periodHours = Substitute.For<IPeriodHoursService>();
        _cascade = Substitute.For<IOvertimeCascadeService>();
        _queue = Substitute.For<IThoroughRecalculationQueue>();
        _logger = Substitute.For<ILogger<ScheduleCompletionService>>();

        _clientId = Guid.NewGuid();
        _previousClientId = Guid.NewGuid();
        _analyseToken = Guid.NewGuid();

        var context = new DefaultHttpContext();
        context.Request.Headers[HttpHeaderNames.SignalRConnectionId] = ConnectionId;
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        httpContextAccessor.HttpContext.Returns(context);

        _queue.QueueRecalculation(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyCollection<Guid>?>())
            .Returns(true);
        _periodHours.GetPeriodBoundariesAsync(FollowingStart).Returns((FollowingStart, FollowingEnd));
        _periodHours.RecalculateAndNotifyAsync(_clientId, PeriodStart, PeriodEnd, _analyseToken, ConnectionId)
            .Returns(new PeriodHoursResource { Hours = RecalculatedHours });
        _periodHours.RecalculateAndNotifyAsync(_previousClientId, PeriodStart, PeriodEnd, _analyseToken, ConnectionId)
            .Returns(new PeriodHoursResource { Hours = PreviousRecalculatedHours });
        _periodHours.CalculatePeriodHoursAsync(_clientId, PeriodStart, PeriodEnd, _analyseToken)
            .Returns(new PeriodHoursResource { Hours = LiveHours });
        _periodHours.CalculatePeriodHoursAsync(_previousClientId, PeriodStart, PeriodEnd, _analyseToken)
            .Returns(new PeriodHoursResource { Hours = PreviousLiveHours });

        _service = new ScheduleCompletionService(
            _unitOfWork, _changeTracker, _timeline, _periodHours, httpContextAccessor, _cascade, _queue, _logger);
    }

    [Test]
    public async Task Success_QueuesTheCheckReprocessesTracksAndRecalculates_InThatOrder_AndNeverRepairs()
    {
        var work = NewWork(_clientId, Day);

        var completion = await _service.CompleteWorkWriteAsync(work, null, PeriodStart, PeriodEnd);

        completion.PeriodHours.ShouldNotBeNull().Hours.ShouldBe(RecalculatedHours);
        completion.PreviousPeriodHours.ShouldBeNull();
        Received.InOrder(() =>
        {
            _timeline.QueueCheck(_clientId, Day, _analyseToken);
            _cascade.ReprocessSuccessorsAsync(work, null);
            _changeTracker.TrackChangeAsync(_clientId, Day, _analyseToken);
            _periodHours.RecalculateAndNotifyAsync(_clientId, PeriodStart, PeriodEnd, _analyseToken, ConnectionId);
        });
        ThenNothingWasRepaired();
        await _unitOfWork.DidNotReceive().CompleteAsync();
        ErrorLogCount().ShouldBe(0);
    }

    [Test]
    public async Task CascadeThrows_DiscardsQueuesThePeriodInvalidatesTheDay_AndReturnsTheLiveHours()
    {
        var work = NewWork(_clientId, Day);
        _cascade.ReprocessSuccessorsAsync(work, null).Returns<Task>(_ => throw new Exception(InjectedFailureMessage));

        var completion = await _service.CompleteWorkWriteAsync(work, null, PeriodStart, PeriodEnd);

        completion.PeriodHours.ShouldNotBeNull().Hours.ShouldBe(LiveHours);
        completion.PreviousPeriodHours.ShouldBeNull();
        Received.InOrder(() =>
        {
            _timeline.QueueCheck(_clientId, Day, _analyseToken);
            _cascade.ReprocessSuccessorsAsync(work, null);
            _unitOfWork.DiscardTrackedChanges();
            _queue.QueueRecalculation(PeriodStart, PeriodEnd, NoGroup(), _analyseToken, Clients(_clientId));
            _periodHours.InvalidateCacheAsync(_clientId, Day, _analyseToken);
            _periodHours.CalculatePeriodHoursAsync(_clientId, PeriodStart, PeriodEnd, _analyseToken);
        });
        _queue.ReceivedCalls().Count().ShouldBe(1);
        await _changeTracker.DidNotReceiveWithAnyArgs().TrackChangeAsync(default, default, default);
        await _periodHours.DidNotReceiveWithAnyArgs().RecalculateAndNotifyAsync(default, default, default, default, default);
        ErrorLogCount().ShouldBe(1);
    }

    [Test]
    public async Task RecalculationThrows_AfterTheCascade_DiscardsQueuesInvalidates_AndReturnsTheLiveHours()
    {
        var work = NewWork(_clientId, Day);
        _periodHours.RecalculateAndNotifyAsync(_clientId, PeriodStart, PeriodEnd, _analyseToken, ConnectionId)
            .Returns<Task<PeriodHoursResource>>(_ => throw new Exception(InjectedFailureMessage));

        var completion = await _service.CompleteWorkWriteAsync(work, null, PeriodStart, PeriodEnd);

        completion.PeriodHours.ShouldNotBeNull().Hours.ShouldBe(LiveHours);
        Received.InOrder(() =>
        {
            _cascade.ReprocessSuccessorsAsync(work, null);
            _changeTracker.TrackChangeAsync(_clientId, Day, _analyseToken);
            _periodHours.RecalculateAndNotifyAsync(_clientId, PeriodStart, PeriodEnd, _analyseToken, ConnectionId);
            _unitOfWork.DiscardTrackedChanges();
            _queue.QueueRecalculation(PeriodStart, PeriodEnd, NoGroup(), _analyseToken, Clients(_clientId));
            _periodHours.InvalidateCacheAsync(_clientId, Day, _analyseToken);
            _periodHours.CalculatePeriodHoursAsync(_clientId, PeriodStart, PeriodEnd, _analyseToken);
        });
        ErrorLogCount().ShouldBe(1);
    }

    [Test]
    public async Task Failure_OnTheLastDayWhoseWeekStaysInThePeriod_QueuesOnlyThePeriod()
    {
        var work = GivenFailingCascadeFor(NewWork(_clientId, LastDayWithoutLookAhead));

        await _service.CompleteWorkWriteAsync(work, null, PeriodStart, PeriodEnd);

        _queue.Received(1).QueueRecalculation(PeriodStart, PeriodEnd, NoGroup(), _analyseToken, Clients(_clientId));
        _queue.ReceivedCalls().Count().ShouldBe(1);
        await _periodHours.DidNotReceiveWithAnyArgs().GetPeriodBoundariesAsync(default);
    }

    [Test]
    public async Task Failure_OnTheFirstDayWhoseWeekReachesTheNextPeriod_QueuesTheFollowingPeriodBeforeThePeriod()
    {
        var work = GivenFailingCascadeFor(NewWork(_clientId, FirstDayWithLookAhead));

        await _service.CompleteWorkWriteAsync(work, null, PeriodStart, PeriodEnd);

        Received.InOrder(() =>
        {
            _periodHours.GetPeriodBoundariesAsync(FollowingStart);
            _queue.QueueRecalculation(FollowingStart, FollowingEnd, NoGroup(), _analyseToken, Clients(_clientId));
            _queue.QueueRecalculation(PeriodStart, PeriodEnd, NoGroup(), _analyseToken, Clients(_clientId));
        });
        _queue.ReceivedCalls().Count().ShouldBe(2);
    }

    [Test]
    public async Task Failure_WhenTheFollowingPeriodCannotBeResolved_StillQueuesThePeriod_AndDoesNotThrow()
    {
        var work = GivenFailingCascadeFor(NewWork(_clientId, FirstDayWithLookAhead));
        _periodHours.GetPeriodBoundariesAsync(FollowingStart)
            .Returns<Task<(DateOnly StartDate, DateOnly EndDate)>>(_ => throw new Exception(InjectedFailureMessage));

        var completion = await _service.CompleteWorkWriteAsync(work, null, PeriodStart, PeriodEnd);

        completion.PeriodHours.ShouldNotBeNull().Hours.ShouldBe(LiveHours);
        _queue.Received(1).QueueRecalculation(PeriodStart, PeriodEnd, NoGroup(), _analyseToken, Clients(_clientId));
        _queue.ReceivedCalls().Count().ShouldBe(1);
        ErrorLogCount().ShouldBe(2);
    }

    [Test]
    public async Task Failure_WhenTheInvalidationThrows_DoesNotEscape_AndStillReturnsTheLiveHours()
    {
        var work = GivenFailingCascadeFor(NewWork(_clientId, Day));
        _periodHours.InvalidateCacheAsync(_clientId, Day, _analyseToken)
            .Returns<Task>(_ => throw new Exception(InjectedFailureMessage));

        var completion = await _service.CompleteWorkWriteAsync(work, null, PeriodStart, PeriodEnd);

        completion.PeriodHours.ShouldNotBeNull().Hours.ShouldBe(LiveHours);
        await _periodHours.Received(1).CalculatePeriodHoursAsync(_clientId, PeriodStart, PeriodEnd, _analyseToken);
        ErrorLogCount().ShouldBe(2);
    }

    [Test]
    public async Task Failure_WhenTheQueueRejects_LogsTheRejection_AndDoesNotThrow()
    {
        var work = GivenFailingCascadeFor(NewWork(_clientId, Day));
        _queue.QueueRecalculation(PeriodStart, PeriodEnd, NoGroup(), _analyseToken, Clients(_clientId)).Returns(false);

        var completion = await _service.CompleteWorkWriteAsync(work, null, PeriodStart, PeriodEnd);

        completion.PeriodHours.ShouldNotBeNull().Hours.ShouldBe(LiveHours);
        await _periodHours.Received(1).InvalidateCacheAsync(_clientId, Day, _analyseToken);
        ErrorLogCount().ShouldBe(2);
    }

    [Test]
    public async Task Failure_WhenTheLiveHoursCannotBeRead_AnswersWithoutPeriodHours_AndDoesNotThrow()
    {
        var work = GivenFailingCascadeFor(NewWork(_clientId, Day));
        _periodHours.CalculatePeriodHoursAsync(_clientId, PeriodStart, PeriodEnd, _analyseToken)
            .Returns<Task<PeriodHoursResource>>(_ => throw new Exception(InjectedFailureMessage));

        var completion = await _service.CompleteWorkWriteAsync(work, null, PeriodStart, PeriodEnd);

        completion.PeriodHours.ShouldBeNull();
        completion.PreviousPeriodHours.ShouldBeNull();
        ErrorLogCount().ShouldBe(2);
    }

    [Test]
    public async Task MoveToAnotherClient_Success_QueuesTracksAndRecalculatesBothClients()
    {
        var previous = NewWork(_previousClientId, Day.AddDays(-1));
        var work = NewWork(_clientId, Day);

        var completion = await _service.CompleteWorkWriteAsync(work, previous, PeriodStart, PeriodEnd);

        completion.PeriodHours.ShouldNotBeNull().Hours.ShouldBe(RecalculatedHours);
        completion.PreviousPeriodHours.ShouldNotBeNull().Hours.ShouldBe(PreviousRecalculatedHours);
        Received.InOrder(() =>
        {
            _timeline.QueueCheck(_clientId, Day, _analyseToken);
            _timeline.QueueCheck(_previousClientId, Day.AddDays(-1), _analyseToken);
            _cascade.ReprocessSuccessorsAsync(work, previous);
            _changeTracker.TrackChangeAsync(_clientId, Day, _analyseToken);
            _changeTracker.TrackChangeAsync(_previousClientId, Day.AddDays(-1), _analyseToken);
            _periodHours.RecalculateAndNotifyAsync(_clientId, PeriodStart, PeriodEnd, _analyseToken, ConnectionId);
            _periodHours.RecalculateAndNotifyAsync(_previousClientId, PeriodStart, PeriodEnd, _analyseToken, ConnectionId);
        });
        ThenNothingWasRepaired();
    }

    [Test]
    public async Task MoveToAnotherClient_Failure_QueuesBothClients_InvalidatesBothDays_AndReturnsBothLiveHours()
    {
        var previous = NewWork(_previousClientId, Day.AddDays(-1));
        var work = NewWork(_clientId, Day);
        _cascade.ReprocessSuccessorsAsync(work, previous).Returns<Task>(_ => throw new Exception(InjectedFailureMessage));

        var completion = await _service.CompleteWorkWriteAsync(work, previous, PeriodStart, PeriodEnd);

        completion.PeriodHours.ShouldNotBeNull().Hours.ShouldBe(LiveHours);
        completion.PreviousPeriodHours.ShouldNotBeNull().Hours.ShouldBe(PreviousLiveHours);
        _queue.Received(1).QueueRecalculation(
            PeriodStart, PeriodEnd, NoGroup(), _analyseToken, Clients(_clientId, _previousClientId));
        _queue.ReceivedCalls().Count().ShouldBe(1);
        await _periodHours.Received(1).InvalidateCacheAsync(_clientId, Day, _analyseToken);
        await _periodHours.Received(1).InvalidateCacheAsync(_previousClientId, Day.AddDays(-1), _analyseToken);
        _timeline.Received(1).QueueCheck(_previousClientId, Day.AddDays(-1), _analyseToken);
    }

    [Test]
    public async Task MoveToAnotherDayOfTheSameClient_TracksBothDays_AndRecalculatesTheClientOnce()
    {
        var previous = NewWork(_clientId, Day.AddDays(-1));
        var work = NewWork(_clientId, Day);

        var completion = await _service.CompleteWorkWriteAsync(work, previous, PeriodStart, PeriodEnd);

        completion.PreviousPeriodHours.ShouldBeNull();
        await _changeTracker.Received(1).TrackChangeAsync(_clientId, Day, _analyseToken);
        await _changeTracker.Received(1).TrackChangeAsync(_clientId, Day.AddDays(-1), _analyseToken);
        _timeline.Received(1).QueueCheck(_clientId, Day.AddDays(-1), _analyseToken);
        await _periodHours.Received(1).RecalculateAndNotifyAsync(
            _clientId, PeriodStart, PeriodEnd, _analyseToken, ConnectionId);
        _periodHours.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(IPeriodHoursService.RecalculateAndNotifyAsync))
            .ShouldBe(1);
    }

    [Test]
    public async Task MoveBackFromTheFollowingPayPeriod_AlsoStoresThePeriodItLeft_WithoutAPush()
    {
        var previous = NewWork(_clientId, FollowingStart);
        var work = NewWork(_clientId, Day);
        _periodHours.GetPeriodBoundariesAsync(FollowingStart).Returns((FollowingStart, FollowingEnd));

        var completion = await _service.CompleteWorkWriteAsync(work, previous, PeriodStart, PeriodEnd);

        completion.PeriodHours.ShouldNotBeNull().Hours.ShouldBe(RecalculatedHours);
        completion.PreviousPeriodHours.ShouldBeNull();
        Received.InOrder(() =>
        {
            _periodHours.RecalculateAndNotifyAsync(_clientId, PeriodStart, PeriodEnd, _analyseToken, ConnectionId);
            _periodHours.RecalculatePeriodHoursAsync(_clientId, FollowingStart, FollowingEnd, _analyseToken);
        });
        await _periodHours.DidNotReceive().RecalculateAndNotifyAsync(
            _clientId, FollowingStart, FollowingEnd, Arg.Any<Guid?>(), Arg.Any<string?>());
        ThenNothingWasRepaired();
    }

    [Test]
    public async Task MoveToAnotherClientAndPayPeriod_StoresThePreviousClientInThePeriodItLeft()
    {
        var previous = NewWork(_previousClientId, FollowingStart);
        var work = NewWork(_clientId, Day);
        _periodHours.GetPeriodBoundariesAsync(FollowingStart).Returns((FollowingStart, FollowingEnd));

        await _service.CompleteWorkWriteAsync(work, previous, PeriodStart, PeriodEnd);

        await _periodHours.Received(1).RecalculatePeriodHoursAsync(
            _previousClientId, FollowingStart, FollowingEnd, _analyseToken);
        StoredRowCount().ShouldBe(1);
    }

    [Test]
    public async Task Success_OnTheFirstDayWhoseWeekReachesTheNextPeriod_AlsoStoresTheFollowingPeriod_WithoutAPush()
    {
        var work = NewWork(_clientId, FirstDayWithLookAhead);

        await _service.CompleteWorkWriteAsync(work, null, PeriodStart, PeriodEnd);

        Received.InOrder(() =>
        {
            _periodHours.RecalculateAndNotifyAsync(_clientId, PeriodStart, PeriodEnd, _analyseToken, ConnectionId);
            _periodHours.GetPeriodBoundariesAsync(FollowingStart);
            _periodHours.RecalculatePeriodHoursAsync(_clientId, FollowingStart, FollowingEnd, _analyseToken);
        });
        StoredRowCount().ShouldBe(1);
        ThenNothingWasRepaired();
    }

    [Test]
    public async Task Success_OnTheLastDayWhoseWeekStaysInThePeriod_StoresNoOtherPeriod()
    {
        var work = NewWork(_clientId, LastDayWithoutLookAhead);

        await _service.CompleteWorkWriteAsync(work, null, PeriodStart, PeriodEnd);

        await _periodHours.DidNotReceiveWithAnyArgs().GetPeriodBoundariesAsync(default);
        StoredRowCount().ShouldBe(0);
    }

    [Test]
    public async Task MoveIntoTheFollowingPeriod_StoresTheLeftPeriod_AndDoesNotStoreTheNewPeriodTwice()
    {
        var previous = NewWork(_clientId, FirstDayWithLookAhead);
        var work = NewWork(_clientId, FollowingStart.AddDays(1));
        _periodHours.GetPeriodBoundariesAsync(FirstDayWithLookAhead).Returns((PeriodStart, PeriodEnd));

        await _service.CompleteWorkWriteAsync(work, previous, FollowingStart, FollowingEnd);

        await _periodHours.Received(1).RecalculatePeriodHoursAsync(_clientId, PeriodStart, PeriodEnd, _analyseToken);
        StoredRowCount().ShouldBe(1);
    }

    [Test]
    public async Task MoveWithinThePayPeriod_DoesNotResolveAnotherPeriod()
    {
        var previous = NewWork(_previousClientId, Day.AddDays(-1));
        var work = NewWork(_clientId, Day);

        await _service.CompleteWorkWriteAsync(work, previous, PeriodStart, PeriodEnd);

        await _periodHours.DidNotReceiveWithAnyArgs().GetPeriodBoundariesAsync(default);
    }

    private int StoredRowCount()
        => _periodHours.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(IPeriodHoursService.RecalculatePeriodHoursAsync));

    private Work NewWork(Guid clientId, DateOnly date) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        CurrentDate = date,
        AnalyseToken = _analyseToken
    };

    private Work GivenFailingCascadeFor(Work work)
    {
        _cascade.ReprocessSuccessorsAsync(work, null).Returns<Task>(_ => throw new Exception(InjectedFailureMessage));
        return work;
    }

    private void ThenNothingWasRepaired()
    {
        _unitOfWork.DidNotReceive().DiscardTrackedChanges();
        _queue.ReceivedCalls().ShouldBeEmpty();
        _periodHours.DidNotReceiveWithAnyArgs().InvalidateCacheAsync(default, default, default);
        _periodHours.DidNotReceiveWithAnyArgs().CalculatePeriodHoursAsync(default, default, default, default);
    }

    private int ErrorLogCount()
        => _logger.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(ILogger.Log)
                && call.GetArguments()[0] is LogLevel.Error);

    private static Guid? NoGroup() => Arg.Is<Guid?>(group => group == null);

    private static IReadOnlyCollection<Guid>? Clients(params Guid[] clientIds)
        => Arg.Is<IReadOnlyCollection<Guid>?>(ids => ids != null && ids.SequenceEqual(clientIds));
}
