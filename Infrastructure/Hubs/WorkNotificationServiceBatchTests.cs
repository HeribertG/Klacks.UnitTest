// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Infrastructure.Hubs;

/// <summary>
/// Two properties of the recipient resolution that a process-local tracker hides. First, the group
/// filter is a visibility boundary: a connection that picked a group must never see collisions of
/// clients outside it. Second, resolving the recipients has to cost one tracker call no matter how
/// many dates a notification spans - over a network every extra call is a round trip.
/// </summary>
[TestFixture]
public sealed class WorkNotificationServiceBatchTests
{
    private static readonly Guid GroupOne = Guid.NewGuid();
    private static readonly Guid GroupTwo = Guid.NewGuid();
    private static readonly Guid ClientInGroupOne = Guid.NewGuid();
    private static readonly Guid ClientInGroupTwo = Guid.NewGuid();
    private static readonly DateOnly AnyDate = new(2026, 7, 15);

    private IHubContext<WorkNotificationHub, IScheduleClient> _hubContext = null!;
    private IHubClients<IScheduleClient> _clients = null!;
    private IConnectionDateRangeTracker _tracker = null!;
    private IGetAllClientIdsFromGroupAndSubgroups _groupClient = null!;
    private WorkNotificationService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _clients = Substitute.For<IHubClients<IScheduleClient>>();
        _hubContext = Substitute.For<IHubContext<WorkNotificationHub, IScheduleClient>>();
        _hubContext.Clients.Returns(_clients);

        _tracker = Substitute.For<IConnectionDateRangeTracker>();
        _groupClient = Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>();
        _groupClient.GetAllClientIdsFromGroupAndSubgroups(GroupOne).Returns([ClientInGroupOne]);
        _groupClient.GetAllClientIdsFromGroupAndSubgroups(GroupTwo).Returns([ClientInGroupTwo]);

        _sut = new WorkNotificationService(
            _hubContext,
            _tracker,
            _groupClient,
            NullLogger<WorkNotificationService>.Instance);
    }

    [Test]
    public async Task FullRefresh_EachGroupConnectionOnlySeesItsOwnCollisions()
    {
        var groupOneClient = ClientFor("conn-group-one");
        var groupTwoClient = ClientFor("conn-group-two");

        _tracker.GetConnectionsGroupedBySelectedGroupAsync(null).Returns(new GroupedScheduleConnections(
            [],
            new Dictionary<Guid, IReadOnlyList<ScheduleConnectionSnapshot>>
            {
                [GroupOne] = [Snapshot("conn-group-one", GroupOne)],
                [GroupTwo] = [Snapshot("conn-group-two", GroupTwo)]
            }));

        await _sut.NotifyCollisionsDetected(FullRefresh());

        await groupOneClient.Received(1).CollisionsDetected(Arg.Is<CollisionListNotificationDto>(
            dto => dto.Collisions.Count == 1 && dto.Collisions[0].ClientId == ClientInGroupOne));
        await groupTwoClient.Received(1).CollisionsDetected(Arg.Is<CollisionListNotificationDto>(
            dto => dto.Collisions.Count == 1 && dto.Collisions[0].ClientId == ClientInGroupTwo));
    }

    [Test]
    public async Task FullRefresh_ConnectionWithoutAGroupSelectionSeesEverything()
    {
        var ungroupedClient = ClientFor("conn-all");

        _tracker.GetConnectionsGroupedBySelectedGroupAsync(null).Returns(new GroupedScheduleConnections(
            [Snapshot("conn-all", null)],
            new Dictionary<Guid, IReadOnlyList<ScheduleConnectionSnapshot>>()));

        await _sut.NotifyCollisionsDetected(FullRefresh());

        await ungroupedClient.Received(1).CollisionsDetected(Arg.Is<CollisionListNotificationDto>(
            dto => dto.Collisions.Count == 2));
    }

    [Test]
    public async Task ThirtyDates_ResolveTheRecipientsInASingleTrackerCall()
    {
        _tracker.GetConnectionsForDatesAsync(
                Arg.Any<IReadOnlyCollection<DateOnly>>(), Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([Snapshot("conn-all", null)]);

        await _sut.NotifyCollisionsDetected(PartialRefreshAcross(dateCount: 30));

        await _tracker.Received(1).GetConnectionsForDatesAsync(
            Arg.Any<IReadOnlyCollection<DateOnly>>(), Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ThirtyDates_HandOverEveryDateToTheBatchCall()
    {
        _tracker.GetConnectionsForDatesAsync(
                Arg.Any<IReadOnlyCollection<DateOnly>>(), Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([Snapshot("conn-all", null)]);

        await _sut.NotifyCollisionsDetected(PartialRefreshAcross(dateCount: 30));

        await _tracker.Received(1).GetConnectionsForDatesAsync(
            Arg.Is<IReadOnlyCollection<DateOnly>>(dates => dates.Count == 30),
            Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private IScheduleClient ClientFor(string connectionId)
    {
        var client = Substitute.For<IScheduleClient>();
        _clients.Clients(Arg.Is<IReadOnlyList<string>>(ids => ids.Contains(connectionId))).Returns(client);
        return client;
    }

    private static ScheduleConnectionSnapshot Snapshot(string connectionId, Guid? selectedGroupId)
        => new(connectionId, AnyDate, AnyDate, null, selectedGroupId);

    private static CollisionListNotificationDto FullRefresh()
        => new()
        {
            IsFullRefresh = true,
            Collisions =
            [
                new CollisionNotificationDto { ClientId = ClientInGroupOne, Date = AnyDate },
                new CollisionNotificationDto { ClientId = ClientInGroupTwo, Date = AnyDate }
            ]
        };

    private static CollisionListNotificationDto PartialRefreshAcross(int dateCount)
        => new()
        {
            IsFullRefresh = false,
            Collisions = Enumerable.Range(0, dateCount)
                .Select(offset => new CollisionNotificationDto
                {
                    ClientId = ClientInGroupOne,
                    Date = AnyDate.AddDays(offset)
                })
                .ToList()
        };
}
