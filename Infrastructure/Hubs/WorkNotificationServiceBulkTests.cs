// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Hubs;

/// <summary>
/// Applying a wizard result used to send one WorkCreated event per assignment. The batch event must
/// resolve its target connections once and send once, and it must stay silent when no connection covers
/// the range - otherwise the saving is spent again on a broadcast nobody listens to.
/// </summary>
[TestFixture]
public sealed class WorkNotificationServiceBulkTests
{
    private const string SourceConnection = "source-connection";
    private static readonly DateOnly RangeStart = new(2026, 7, 1);
    private static readonly DateOnly RangeEnd = new(2026, 7, 31);

    private IHubContext<WorkNotificationHub, IScheduleClient> _hubContext = null!;
    private IConnectionDateRangeTracker _tracker = null!;
    private IScheduleClient _client = null!;
    private WorkNotificationService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _client = Substitute.For<IScheduleClient>();
        _hubContext = Substitute.For<IHubContext<WorkNotificationHub, IScheduleClient>>();
        var clients = Substitute.For<IHubClients<IScheduleClient>>();
        clients.Clients(Arg.Any<IReadOnlyList<string>>()).Returns(_client);
        _hubContext.Clients.Returns(clients);

        _tracker = Substitute.For<IConnectionDateRangeTracker>();
        _sut = new WorkNotificationService(
            _hubContext,
            _tracker,
            Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>(),
            NullLogger<WorkNotificationService>.Instance);
    }

    [Test]
    public async Task NotifyWorksBulkCreated_SendsExactlyOneEventForTheWholeBatch()
    {
        _tracker.GetConnectionsForDateRange(RangeStart, RangeEnd, null, SourceConnection)
            .Returns(["connection-a", "connection-b"]);

        var notification = Batch(workCount: 25);

        await _sut.NotifyWorksBulkCreated(notification);

        await _client.Received(1).WorksBulkCreated(Arg.Is<WorksBulkCreatedNotificationDto>(n => n.Works.Count == 25));
    }

    [Test]
    public async Task NotifyWorksBulkCreated_ResolvesTheConnectionsOnce()
    {
        _tracker.GetConnectionsForDateRange(RangeStart, RangeEnd, null, SourceConnection)
            .Returns(["connection-a"]);

        await _sut.NotifyWorksBulkCreated(Batch(workCount: 25));

        _tracker.Received(1).GetConnectionsForDateRange(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<string>());
    }

    [Test]
    public async Task NotifyWorksBulkCreated_NoMatchingConnection_SendsNothing()
    {
        _tracker.GetConnectionsForDateRange(RangeStart, RangeEnd, null, SourceConnection)
            .Returns([]);

        await _sut.NotifyWorksBulkCreated(Batch(workCount: 5));

        await _client.DidNotReceiveWithAnyArgs().WorksBulkCreated(default!);
    }

    [Test]
    public async Task NotifyWorksBulkCreated_EmptyBatch_NeverTouchesTheTracker()
    {
        await _sut.NotifyWorksBulkCreated(Batch(workCount: 0));

        _tracker.DidNotReceiveWithAnyArgs().GetConnectionsForDateRange(default, default, default, default!);
    }

    private static WorksBulkCreatedNotificationDto Batch(int workCount)
    {
        var works = Enumerable.Range(0, workCount)
            .Select(_ => new WorkNotificationDto
            {
                WorkId = Guid.NewGuid(),
                ClientId = Guid.NewGuid(),
                CurrentDate = RangeStart,
            })
            .ToList();

        return new WorksBulkCreatedNotificationDto(works, RangeStart, RangeEnd, SourceConnection, null);
    }
}
