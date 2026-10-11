// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Infrastructure.Hubs;

/// <summary>
/// Group visibility is a boundary for schedule notifications too: a connection without a group filter used to receive
/// the notifications of every group. A restricted user's connection only receives notifications about clients of the
/// user's visible groups (cached on the connection state), an unrestricted user's (admin) connection keeps receiving
/// everything, and a connection whose visibility was never resolved receives nothing.
/// </summary>
[TestFixture]
public sealed class WorkNotificationServiceVisibilityTests
{
    private const string RestrictedConnection = "conn-restricted";
    private const string AdminConnection = "conn-admin";
    private const string CandidateChangeKind = "Created";
    private static readonly Guid OwnGroup = Guid.NewGuid();
    private static readonly Guid ForeignGroup = Guid.NewGuid();
    private static readonly Guid OwnClient = Guid.NewGuid();
    private static readonly Guid ForeignClient = Guid.NewGuid();
    private static readonly DateOnly AnyDate = new(2026, 7, 15);

    private IHubClients<IScheduleClient> _clients = null!;
    private IConnectionDateRangeTracker _tracker = null!;
    private WorkNotificationService _sut = null!;
    private readonly List<IReadOnlyList<string>> _addressed = [];

    [SetUp]
    public void SetUp()
    {
        _addressed.Clear();
        _clients = Substitute.For<IHubClients<IScheduleClient>>();
        _clients.Clients(Arg.Do<IReadOnlyList<string>>(ids => _addressed.Add(ids))).Returns(Substitute.For<IScheduleClient>());
        var hubContext = Substitute.For<IHubContext<WorkNotificationHub, IScheduleClient>>();
        hubContext.Clients.Returns(_clients);

        _tracker = Substitute.For<IConnectionDateRangeTracker>();
        _tracker.GetConnectionsForDateRangeAsync(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([Restricted(), Admin()]);
        _tracker.GetConnectionsForDatesAsync(
                Arg.Any<IReadOnlyCollection<DateOnly>>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([Restricted(), Admin()]);
        _tracker.GetConnectionsGroupedBySelectedGroupAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new GroupedScheduleConnections(
                [Restricted(), Admin()], new Dictionary<Guid, IReadOnlyList<ScheduleConnectionSnapshot>>()));

        _sut = new WorkNotificationService(
            hubContext, _tracker, Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>(), NullLogger<WorkNotificationService>.Instance);
    }

    [Test]
    public async Task WorkOfAForeignGroupsClient_ReachesOnlyTheUnrestrictedConnection()
    {
        await _sut.NotifyWorkCreated(Work(ForeignClient));

        AddressedConnections().ShouldBe(new[] { AdminConnection });
    }

    [Test]
    public async Task WorkOfTheOwnGroupsClient_ReachesTheRestrictedConnectionToo()
    {
        await _sut.NotifyWorkUpdated(Work(OwnClient));

        AddressedConnections().ShouldBe(new[] { RestrictedConnection, AdminConnection }, ignoreOrder: true);
    }

    [Test]
    public async Task ConnectionWithUnresolvedVisibility_ReceivesNothing()
    {
        _tracker.GetConnectionsForDateRangeAsync(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([new ScheduleConnectionSnapshot("conn-unresolved", AnyDate, AnyDate, null, null, ConnectionVisibility.None)]);

        await _sut.NotifyWorkDeleted(Work(OwnClient));

        AddressedConnections().ShouldBeEmpty();
    }

    [Test]
    public async Task ScheduleAndPeriodHoursUpdates_OfAForeignClient_SkipTheRestrictedConnection()
    {
        await _sut.NotifyScheduleUpdated(new ScheduleNotificationDto { ClientId = ForeignClient, CurrentDate = AnyDate });
        await _sut.NotifyPeriodHoursUpdated(new PeriodHoursNotificationDto { ClientId = ForeignClient, StartDate = AnyDate, EndDate = AnyDate });
        await _sut.NotifyScheduleChangeTracked(new ScheduleChangeNotificationDto { ClientId = ForeignClient, ChangeDate = AnyDate });

        _addressed.ShouldAllBe(ids => !ids.Contains(RestrictedConnection));
        _addressed.Count.ShouldBe(3);
    }

    [Test]
    public async Task BulkCreatedWorks_TheRestrictedConnectionOnlyGetsItsOwnGroupsWorks()
    {
        var restrictedClient = Substitute.For<IScheduleClient>();
        var adminClient = Substitute.For<IScheduleClient>();
        _clients.Clients(Arg.Is<IReadOnlyList<string>>(ids => ids.Contains(RestrictedConnection))).Returns(restrictedClient);
        _clients.Clients(Arg.Is<IReadOnlyList<string>>(ids => ids.Contains(AdminConnection) && !ids.Contains(RestrictedConnection))).Returns(adminClient);

        await _sut.NotifyWorksBulkCreated(new WorksBulkCreatedNotificationDto(
            [Work(OwnClient), Work(ForeignClient)], AnyDate, AnyDate, "sender", null));

        await restrictedClient.Received(1).WorksBulkCreated(Arg.Is<WorksBulkCreatedNotificationDto>(
            dto => dto.Works.Count == 1 && dto.Works[0].ClientId == OwnClient));
        await adminClient.Received(1).WorksBulkCreated(Arg.Is<WorksBulkCreatedNotificationDto>(dto => dto.Works.Count == 2));
    }

    [Test]
    public async Task FullRefreshCollisions_TheRestrictedConnectionWithoutGroupOnlySeesItsOwnGroupsClients()
    {
        var restrictedClient = Substitute.For<IScheduleClient>();
        var adminClient = Substitute.For<IScheduleClient>();
        _clients.Clients(Arg.Is<IReadOnlyList<string>>(ids => ids.Contains(RestrictedConnection))).Returns(restrictedClient);
        _clients.Clients(Arg.Is<IReadOnlyList<string>>(ids => ids.Contains(AdminConnection) && !ids.Contains(RestrictedConnection))).Returns(adminClient);

        await _sut.NotifyCollisionsDetected(new CollisionListNotificationDto
        {
            IsFullRefresh = true,
            Collisions =
            [
                new CollisionNotificationDto { ClientId = OwnClient, Date = AnyDate },
                new CollisionNotificationDto { ClientId = ForeignClient, Date = AnyDate }
            ]
        });

        await restrictedClient.Received(1).CollisionsDetected(Arg.Is<CollisionListNotificationDto>(
            dto => dto.Collisions.Count == 1 && dto.Collisions[0].ClientId == OwnClient));
        await adminClient.Received(1).CollisionsDetected(Arg.Is<CollisionListNotificationDto>(dto => dto.Collisions.Count == 2));
    }

    [Test]
    public async Task SingleDayValidations_OfAForeignClient_AreNotSentToTheRestrictedConnection()
    {
        var restrictedClient = Substitute.For<IScheduleClient>();
        _clients.Clients(Arg.Is<IReadOnlyList<string>>(ids => ids.Contains(RestrictedConnection))).Returns(restrictedClient);

        await _sut.NotifyScheduleValidationsDetected(new ScheduleValidationListNotificationDto
        {
            IsFullRefresh = false,
            CheckedClientId = ForeignClient,
            CheckedDate = AnyDate,
            Entries = [new ScheduleValidationNotificationDto { ClientId = ForeignClient, Date = AnyDate }]
        });

        await restrictedClient.Received(1).ScheduleValidationsDetected(Arg.Is<ScheduleValidationListNotificationDto>(
            dto => dto.Entries.Count == 0));
    }

    [Test]
    public async Task Wizard4Candidates_OfAForeignGroup_SkipTheRestrictedConnectionWithoutGroup()
    {
        await _sut.NotifyWizard4CandidatesChanged(new Wizard4CandidateNotificationDto(null, ForeignGroup, AnyDate, AnyDate, CandidateChangeKind));

        AddressedConnections().ShouldBe(new[] { AdminConnection });
    }

    private string[] AddressedConnections() => _addressed.SelectMany(ids => ids).Distinct().ToArray();

    private static ScheduleConnectionSnapshot Restricted()
        => new(RestrictedConnection, AnyDate, AnyDate, null, null, ConnectionVisibility.Restricted([OwnClient], [OwnGroup]));

    private static ScheduleConnectionSnapshot Admin()
        => new(AdminConnection, AnyDate, AnyDate, null, null, ConnectionVisibility.Unrestricted);

    private static WorkNotificationDto Work(Guid clientId) => new() { ClientId = clientId, CurrentDate = AnyDate };
}
