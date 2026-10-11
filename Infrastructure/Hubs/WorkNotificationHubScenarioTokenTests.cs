// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// JoinScheduleGroup takes an analyseToken from the client. A connection must not join the notification group of a
/// scenario the caller may not see (it would receive that scenario's change notifications); visible scenarios and the
/// main plan join as before.
/// </summary>

using Klacks.Api.Infrastructure.Hubs;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Hubs;

[TestFixture]
public class WorkNotificationHubScenarioTokenTests
{
    private const string StartDate = "2026-03-02";
    private const string EndDate = "2026-03-08";

    private readonly Guid _hiddenToken = Guid.NewGuid();
    private IGroupManager _groups = null!;
    private IConnectionDateRangeTracker _tracker = null!;

    private WorkNotificationHub Hub()
    {
        _groups = Substitute.For<IGroupManager>();
        _tracker = Substitute.For<IConnectionDateRangeTracker>();
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns("connection-1");

        return new WorkNotificationHub(
            NullLogger<WorkNotificationHub>.Instance,
            _tracker,
            Substitute.For<IScheduleTimelineService>(),
            TestScenarioTokenAccess.Hiding(_hiddenToken))
        {
            Context = context,
            Groups = _groups
        };
    }

    [Test]
    public async Task JoinScheduleGroup_OfHiddenScenario_DoesNotJoin()
    {
        var hub = Hub();

        await hub.JoinScheduleGroup(StartDate, EndDate, _hiddenToken.ToString());

        await _groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
        await _tracker.DidNotReceiveWithAnyArgs().RegisterConnectionAsync(default!, default, default, default, default);
    }

    [Test]
    public async Task JoinScheduleGroup_OfVisibleScenario_Joins()
    {
        var hub = Hub();

        await hub.JoinScheduleGroup(StartDate, EndDate, Guid.NewGuid().ToString());

        await _groups.ReceivedWithAnyArgs(1).AddToGroupAsync(default!, default!, default);
    }
}
