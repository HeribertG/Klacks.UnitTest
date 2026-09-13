// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ScheduleCompletionService.SaveAndTrackMoveAsync: verifies that moving a
/// schedule entry to another client recalculates ClientPeriodHours for BOTH the old and the
/// new client, not just the new one (regression for the stale-sum-on-old-client drag-drop bug).
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.Interfaces;
using Microsoft.AspNetCore.Http;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class ScheduleCompletionServiceTests
{
    private IUnitOfWork _unitOfWork = null!;
    private IScheduleChangeTracker _scheduleChangeTracker = null!;
    private IScheduleTimelineService _timelineService = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private ScheduleCompletionService _service = null!;

    private readonly Guid _targetClientId = Guid.NewGuid();
    private readonly Guid _sourceClientId = Guid.NewGuid();
    private readonly DateOnly _date = new(2027, 3, 1);
    private readonly DateOnly _periodStart = new(2027, 3, 1);
    private readonly DateOnly _periodEnd = new(2027, 3, 31);

    [SetUp]
    public void Setup()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _scheduleChangeTracker = Substitute.For<IScheduleChangeTracker>();
        _timelineService = Substitute.For<IScheduleTimelineService>();
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();

        _periodHoursService.RecalculateAndNotifyAsync(_targetClientId, _periodStart, _periodEnd, null, Arg.Any<string?>())
            .Returns(new PeriodHoursResource { Hours = 8 });
        _periodHoursService.RecalculateAndNotifyAsync(_sourceClientId, _periodStart, _periodEnd, null, Arg.Any<string?>())
            .Returns(new PeriodHoursResource { Hours = 3 });

        _service = new ScheduleCompletionService(
            _unitOfWork, _scheduleChangeTracker, _timelineService, _periodHoursService, _httpContextAccessor);
    }

    [Test]
    public async Task SaveAndTrackMoveAsync_RecalculatesPeriodHours_ForBothOldAndNewClient()
    {
        var (periodHours, previousPeriodHours) = await _service.SaveAndTrackMoveAsync(
            _targetClientId, _date, _periodStart, _periodEnd, _sourceClientId, _date, null);

        periodHours.Hours.ShouldBe(8);
        previousPeriodHours.ShouldNotBeNull();
        previousPeriodHours!.Hours.ShouldBe(3);

        await _periodHoursService.Received(1).RecalculateAndNotifyAsync(
            _sourceClientId, _periodStart, _periodEnd, null, Arg.Any<string?>());
        await _periodHoursService.Received(1).RecalculateAndNotifyAsync(
            _targetClientId, _periodStart, _periodEnd, null, Arg.Any<string?>());
    }

    [Test]
    public async Task SaveAndTrackMoveAsync_TracksChange_ForBothOldAndNewClient()
    {
        await _service.SaveAndTrackMoveAsync(
            _targetClientId, _date, _periodStart, _periodEnd, _sourceClientId, _date, null);

        await _scheduleChangeTracker.Received(1).TrackChangeAsync(_targetClientId, _date, null);
        await _scheduleChangeTracker.Received(1).TrackChangeAsync(_sourceClientId, _date, null);
    }

    [Test]
    public async Task SaveAndTrackMoveAsync_DoesNotRecalculatePreviousClient_WhenClientDidNotChange()
    {
        var (_, previousPeriodHours) = await _service.SaveAndTrackMoveAsync(
            _targetClientId, _date, _periodStart, _periodEnd, _targetClientId, _date.AddDays(-1), null);

        previousPeriodHours.ShouldBeNull();
        await _periodHoursService.Received(1).RecalculateAndNotifyAsync(
            _targetClientId, _periodStart, _periodEnd, null, Arg.Any<string?>());
    }

    [Test]
    public async Task SaveAndTrackMoveAsync_ReturnsNullPreviousPeriodHours_WhenNoPreviousClientGiven()
    {
        var (periodHours, previousPeriodHours) = await _service.SaveAndTrackMoveAsync(
            _targetClientId, _date, _periodStart, _periodEnd, null, null, null);

        periodHours.Hours.ShouldBe(8);
        previousPeriodHours.ShouldBeNull();
    }
}
