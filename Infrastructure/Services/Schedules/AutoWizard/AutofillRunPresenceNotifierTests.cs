// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Schedules.AutoWizard;
using Klacks.Api.Infrastructure.Hubs;
using Klacks.Api.Infrastructure.Services.Schedules.AutoWizard;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.AutoWizard;

/// <summary>
/// Started and ended autofill runs are sent over the schedule hub to exactly the group audience the tracker names (the
/// connections whose cached visibility allows the run's group), and to nobody when that audience is empty.
/// </summary>
[TestFixture]
public sealed class AutofillRunPresenceNotifierTests
{
    private const string AllowedConnection = "conn-allowed";
    private const string OtherAllowedConnection = "conn-allowed-2";
    private static readonly Guid RunGroup = Guid.NewGuid();
    private static readonly AutofillRunPresenceDto Run =
        new(Guid.NewGuid(), RunGroup, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), ProgressPercent: null);

    private IHubClients<IScheduleClient> _clients = null!;
    private IScheduleClient _addressedClients = null!;
    private IConnectionDateRangeTracker _tracker = null!;
    private AutofillRunPresenceNotifier _sut = null!;
    private readonly List<IReadOnlyList<string>> _addressed = [];

    [SetUp]
    public void SetUp()
    {
        _addressed.Clear();
        _addressedClients = Substitute.For<IScheduleClient>();
        _clients = Substitute.For<IHubClients<IScheduleClient>>();
        _clients.Clients(Arg.Do<IReadOnlyList<string>>(ids => _addressed.Add(ids))).Returns(_addressedClients);
        var hubContext = Substitute.For<IHubContext<WorkNotificationHub, IScheduleClient>>();
        hubContext.Clients.Returns(_clients);

        _tracker = Substitute.For<IConnectionDateRangeTracker>();
        _tracker.GetConnectionsForGroupAudienceAsync(RunGroup, Arg.Any<CancellationToken>())
            .Returns([Connection(AllowedConnection), Connection(OtherAllowedConnection)]);

        _sut = new AutofillRunPresenceNotifier(hubContext, _tracker, NullLogger<AutofillRunPresenceNotifier>.Instance);
    }

    [Test]
    public async Task Started_IsSentToTheGroupAudience()
    {
        await _sut.NotifyStartedAsync(Run);

        _addressed.ShouldHaveSingleItem().ShouldBe(new[] { AllowedConnection, OtherAllowedConnection }, ignoreOrder: true);
        await _addressedClients.Received(1).AutofillRunStarted(Run);
        await _addressedClients.DidNotReceiveWithAnyArgs().AutofillRunEnded(default!);
    }

    [Test]
    public async Task Ended_IsSentToTheGroupAudience()
    {
        await _sut.NotifyEndedAsync(Run);

        _addressed.ShouldHaveSingleItem().ShouldBe(new[] { AllowedConnection, OtherAllowedConnection }, ignoreOrder: true);
        await _addressedClients.Received(1).AutofillRunEnded(Run);
    }

    [Test]
    public async Task EmptyAudience_SendsNothing()
    {
        var groupLessRun = Run with { GroupId = null };
        _tracker.GetConnectionsForGroupAudienceAsync(null, Arg.Any<CancellationToken>()).Returns([]);

        await _sut.NotifyStartedAsync(groupLessRun);

        _addressed.ShouldBeEmpty();
        await _addressedClients.DidNotReceiveWithAnyArgs().AutofillRunStarted(default!);
    }

    private static ScheduleConnectionSnapshot Connection(string connectionId) => new(
        connectionId, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31), AnalyseToken: null, SelectedGroupId: null,
        ConnectionVisibility.Unrestricted);
}
