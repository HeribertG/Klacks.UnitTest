// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using FluentAssertions;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.WorkChanges;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace Klacks.UnitTest.Commands.WorkChanges;

[TestFixture]
public class PutCommandHandlerTests
{
    private IWorkChangeRepository _workChangeRepository = null!;
    private IWorkRepository _workRepository = null!;
    private ScheduleMapper _scheduleMapper = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private IWorkNotificationService _notificationService = null!;
    private IScheduleCompletionService _completionService = null!;
    private IWorkChangeResultService _resultService = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private ILogger<PutCommandHandler> _logger = null!;
    private PutCommandHandler _handler = null!;

    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid WorkId = Guid.NewGuid();
    private static readonly Guid WorkChangeId = Guid.NewGuid();
    private static readonly DateOnly CurrentDate = new(2026, 2, 23);
    private static readonly DateOnly PeriodStart = new(2026, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2026, 2, 28);

    [SetUp]
    public void Setup()
    {
        _workChangeRepository = Substitute.For<IWorkChangeRepository>();
        _workRepository = Substitute.For<IWorkRepository>();
        _scheduleMapper = new ScheduleMapper();
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _notificationService = Substitute.For<IWorkNotificationService>();
        _completionService = Substitute.For<IScheduleCompletionService>();
        _resultService = Substitute.For<IWorkChangeResultService>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _logger = Substitute.For<ILogger<PutCommandHandler>>();

        SetupHttpContext();

        _handler = new PutCommandHandler(
            _workChangeRepository,
            _workRepository,
            _scheduleMapper,
            _periodHoursService,
            _notificationService,
            _completionService,
            _resultService,
            _httpContextAccessor,
            _logger
        );
    }

    [Test]
    public async Task Handle_ReplaceClientChanged_CallsCompletionWithPreviousReplaceClient()
    {
        // Arrange
        var oldReplaceClientId = Guid.NewGuid();
        var newReplaceClientId = Guid.NewGuid();

        SetupExistingWorkChange(oldReplaceClientId);
        SetupUpdatedWorkChange(newReplaceClientId);
        SetupWork();
        SetupPeriodBoundaries();
        SetupResultService();

        var resource = CreateWorkChangeResource(newReplaceClientId);
        var command = new PutCommand<WorkChangeResource>(resource);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        await _completionService.Received(1).SaveAndTrackWithReplaceClientAsync(
            ClientId, CurrentDate, PeriodStart, PeriodEnd,
            newReplaceClientId,
            oldReplaceClientId);
    }

    [Test]
    public async Task Handle_ReplaceClientUnchanged_CallsCompletionWithoutPreviousReplaceClient()
    {
        // Arrange
        var replaceClientId = Guid.NewGuid();

        SetupExistingWorkChange(replaceClientId);
        SetupUpdatedWorkChange(replaceClientId);
        SetupWork();
        SetupPeriodBoundaries();
        SetupResultService();

        var resource = CreateWorkChangeResource(replaceClientId);
        var command = new PutCommand<WorkChangeResource>(resource);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        await _completionService.Received(1).SaveAndTrackWithReplaceClientAsync(
            ClientId, CurrentDate, PeriodStart, PeriodEnd,
            replaceClientId,
            null);
    }

    [Test]
    public async Task Handle_ReplaceClientChanged_ReturnsThreeClientResults()
    {
        // Arrange
        var oldReplaceClientId = Guid.NewGuid();
        var newReplaceClientId = Guid.NewGuid();

        SetupExistingWorkChange(oldReplaceClientId);
        SetupUpdatedWorkChange(newReplaceClientId);
        SetupWork();
        SetupPeriodBoundaries();
        SetupResultService();

        var resource = CreateWorkChangeResource(newReplaceClientId);
        var command = new PutCommand<WorkChangeResource>(resource);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.ClientResults.Should().HaveCount(3);
        result.ClientResults.Should().Contain(r => r.ClientId == ClientId);
        result.ClientResults.Should().Contain(r => r.ClientId == newReplaceClientId);
        result.ClientResults.Should().Contain(r => r.ClientId == oldReplaceClientId);
    }

    [Test]
    public async Task Handle_ReplaceClientChanged_NotifiesAllThreeClients()
    {
        // Arrange
        var oldReplaceClientId = Guid.NewGuid();
        var newReplaceClientId = Guid.NewGuid();

        SetupExistingWorkChange(oldReplaceClientId);
        SetupUpdatedWorkChange(newReplaceClientId);
        SetupWork();
        SetupPeriodBoundaries();
        SetupResultService();

        var resource = CreateWorkChangeResource(newReplaceClientId);
        var command = new PutCommand<WorkChangeResource>(resource);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        await _notificationService.Received(3).NotifyScheduleUpdated(Arg.Any<ScheduleNotificationDto>());
        await _notificationService.Received(1).NotifyScheduleUpdated(
            Arg.Is<ScheduleNotificationDto>(n => n.ClientId == ClientId));
        await _notificationService.Received(1).NotifyScheduleUpdated(
            Arg.Is<ScheduleNotificationDto>(n => n.ClientId == newReplaceClientId));
        await _notificationService.Received(1).NotifyScheduleUpdated(
            Arg.Is<ScheduleNotificationDto>(n => n.ClientId == oldReplaceClientId));
    }

    [Test]
    public async Task Handle_ReplaceClientRemoved_NotifiesMainAndPreviousClient()
    {
        // Arrange
        var oldReplaceClientId = Guid.NewGuid();

        SetupExistingWorkChange(oldReplaceClientId);
        SetupUpdatedWorkChange(null);
        SetupWork();
        SetupPeriodBoundaries();
        SetupResultService();

        var resource = CreateWorkChangeResource(null);
        var command = new PutCommand<WorkChangeResource>(resource);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.ClientResults.Should().HaveCount(2);
        result.ClientResults.Should().Contain(r => r.ClientId == ClientId);
        result.ClientResults.Should().Contain(r => r.ClientId == oldReplaceClientId);

        await _notificationService.Received(2).NotifyScheduleUpdated(Arg.Any<ScheduleNotificationDto>());
        await _completionService.Received(1).SaveAndTrackWithReplaceClientAsync(
            ClientId, CurrentDate, PeriodStart, PeriodEnd,
            (Guid?)null,
            oldReplaceClientId);
    }

    [Test]
    public async Task Handle_NoReplaceClientBefore_OnlyNotifiesMainAndNewClient()
    {
        // Arrange
        var newReplaceClientId = Guid.NewGuid();

        SetupExistingWorkChange(null);
        SetupUpdatedWorkChange(newReplaceClientId);
        SetupWork();
        SetupPeriodBoundaries();
        SetupResultService();

        var resource = CreateWorkChangeResource(newReplaceClientId);
        var command = new PutCommand<WorkChangeResource>(resource);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.ClientResults.Should().HaveCount(2);

        await _notificationService.Received(2).NotifyScheduleUpdated(Arg.Any<ScheduleNotificationDto>());
        await _completionService.Received(1).SaveAndTrackWithReplaceClientAsync(
            ClientId, CurrentDate, PeriodStart, PeriodEnd,
            newReplaceClientId,
            (Guid?)null);
    }

    [Test]
    public async Task Handle_WorkChangeNotFound_ReturnsNull()
    {
        // Arrange
        _workChangeRepository.GetNoTracking(Arg.Any<Guid>()).Returns((WorkChange?)null);

        var resource = CreateWorkChangeResource(null);
        var command = new PutCommand<WorkChangeResource>(resource);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Should().BeNull();
        await _completionService.DidNotReceive().SaveAndTrackWithReplaceClientAsync(
            Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
            Arg.Any<Guid?>(), Arg.Any<Guid?>());
    }

    [Test]
    public async Task Handle_CompletionCalledExactlyOnce_PreventsDuplicateCompleteAsync()
    {
        // Arrange
        var oldReplaceClientId = Guid.NewGuid();
        var newReplaceClientId = Guid.NewGuid();

        SetupExistingWorkChange(oldReplaceClientId);
        SetupUpdatedWorkChange(newReplaceClientId);
        SetupWork();
        SetupPeriodBoundaries();
        SetupResultService();

        var resource = CreateWorkChangeResource(newReplaceClientId);
        var command = new PutCommand<WorkChangeResource>(resource);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        await _completionService.Received(1).SaveAndTrackWithReplaceClientAsync(
            Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
            Arg.Any<Guid?>(), Arg.Any<Guid?>());
    }

    private void SetupHttpContext()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[HttpHeaderNames.SignalRConnectionId] = new StringValues("test-connection");
        _httpContextAccessor.HttpContext.Returns(httpContext);
    }

    private void SetupExistingWorkChange(Guid? replaceClientId)
    {
        var existing = new WorkChange
        {
            Id = WorkChangeId,
            WorkId = WorkId,
            ReplaceClientId = replaceClientId,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
        };
        _workChangeRepository.GetNoTracking(WorkChangeId).Returns(existing);
    }

    private void SetupUpdatedWorkChange(Guid? replaceClientId)
    {
        _workChangeRepository.Put(Arg.Any<WorkChange>()).Returns(callInfo =>
        {
            var wc = callInfo.Arg<WorkChange>();
            wc.ReplaceClientId = replaceClientId;
            return wc;
        });
    }

    private void SetupWork()
    {
        var work = new Work
        {
            Id = WorkId,
            ClientId = ClientId,
            CurrentDate = CurrentDate,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            ShiftId = Guid.NewGuid(),
        };
        _workRepository.Get(WorkId).Returns(work);
    }

    private void SetupPeriodBoundaries()
    {
        _periodHoursService.GetPeriodBoundariesAsync(CurrentDate)
            .Returns((PeriodStart, PeriodEnd));
    }

    private void SetupResultService()
    {
        _resultService.GetClientResultAsync(
            Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new WorkChangeClientResult
            {
                ClientId = callInfo.Arg<Guid>(),
            });
    }

    private static WorkChangeResource CreateWorkChangeResource(Guid? replaceClientId)
    {
        return new WorkChangeResource
        {
            Id = WorkChangeId,
            WorkId = WorkId,
            ReplaceClientId = replaceClientId,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
        };
    }
}
