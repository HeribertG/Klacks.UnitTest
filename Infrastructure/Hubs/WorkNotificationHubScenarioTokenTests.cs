// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// JoinScheduleGroup takes an analyseToken and SetSelectedGroup a group id from the client. A connection must not join
/// the notification group of a scenario the caller may not see, nor filter its notifications onto a hidden group; the
/// main plan, visible scenarios and visible groups work as before. The caller is taken from the connection's own
/// principal (Context.User), not from IHttpContextAccessor, which may be empty inside a hub invocation (long polling)
/// and would then make the guard fail open; a connection without a user id is refused. Exercised with the real token
/// guard, group guard and UserService (no HTTP context); only the user's visibility rows and the scenario lookup are
/// stubbed. The caller's visibility (clients of the visible groups, or unrestricted) is cached on the connection state when it
/// joins a range or changes its group filter, and client group joins are limited to visible clients.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Application.Services.Groups;
using Klacks.Api.Infrastructure.Hubs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Hubs;

[TestFixture]
public class WorkNotificationHubScenarioTokenTests
{
    private const string StartDate = "2026-03-02";
    private const string EndDate = "2026-03-08";
    private const string ConnectionId = "connection-1";

    private Guid _visibleGroupId;
    private Guid _hiddenGroupId;
    private Guid _hiddenToken;
    private Guid _visibleToken;
    private Guid _visibleClientId;
    private Guid _hiddenClientId;
    private IGroupManager _groups = null!;
    private IConnectionDateRangeTracker _tracker = null!;

    [SetUp]
    public void SetUp()
    {
        _visibleGroupId = Guid.NewGuid();
        _hiddenGroupId = Guid.NewGuid();
        _hiddenToken = Guid.NewGuid();
        _visibleToken = Guid.NewGuid();
        _visibleClientId = Guid.NewGuid();
        _hiddenClientId = Guid.NewGuid();
    }

    private WorkNotificationHub Hub(string? userId, bool unrestricted = false)
    {
        _groups = Substitute.For<IGroupManager>();
        _tracker = Substitute.For<IConnectionDateRangeTracker>();

        var visibilityService = Substitute.For<IGroupVisibilityService>();
        visibilityService.GetVisibilityScopeAsync()
            .Returns(Task.FromResult(unrestricted
                ? GroupVisibilityScope.Unrestricted()
                : GroupVisibilityScope.Restricted([_visibleGroupId], [_visibleGroupId])));
        var groupClients = Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>();
        groupClients.GetAllClientIdsFromGroupsAndSubgroupsFromList(Arg.Is<List<Guid>>(ids => ids.Contains(_visibleGroupId)))
            .Returns([_visibleClientId]);
        var scenarios = Substitute.For<IAnalyseScenarioRepository>();
        scenarios.GetByTokenAsync(_hiddenToken, Arg.Any<CancellationToken>())
            .Returns(new AnalyseScenario { Id = Guid.NewGuid(), Token = _hiddenToken, GroupId = _hiddenGroupId });
        scenarios.GetByTokenAsync(_visibleToken, Arg.Any<CancellationToken>())
            .Returns(new AnalyseScenario { Id = Guid.NewGuid(), Token = _visibleToken, GroupId = _visibleGroupId });

        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);
        var userService = new UserService(accessor, null!, Substitute.For<ILogger<UserService>>());
        var groupGuard = new GroupVisibilityGuard(visibilityService, userService);

        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns(ConnectionId);
        var claims = userId is null ? Array.Empty<Claim>() : [new Claim(ClaimTypes.NameIdentifier, userId)];
        context.User.Returns(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));

        return new WorkNotificationHub(
            NullLogger<WorkNotificationHub>.Instance,
            _tracker,
            Substitute.For<IScheduleTimelineService>(),
            new ScenarioTokenAccessGuard(scenarios, groupGuard),
            groupGuard,
            groupClients)
        {
            Context = context,
            Groups = _groups
        };
    }

    [Test]
    public async Task JoinScheduleGroup_OfHiddenScenario_WithoutHttpContext_DoesNotJoin()
    {
        await Hub(Guid.NewGuid().ToString()).JoinScheduleGroup(StartDate, EndDate, _hiddenToken.ToString());

        await _groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
        await _tracker.DidNotReceiveWithAnyArgs().RegisterConnectionAsync(default!, default, default, default, default);
    }

    [Test]
    public async Task JoinScheduleGroup_OfVisibleScenario_Joins()
    {
        await Hub(Guid.NewGuid().ToString()).JoinScheduleGroup(StartDate, EndDate, _visibleToken.ToString());

        await _groups.ReceivedWithAnyArgs(1).AddToGroupAsync(default!, default!, default);
    }

    [Test]
    public async Task JoinScheduleGroup_OfTheMainPlan_Joins()
    {
        await Hub(Guid.NewGuid().ToString()).JoinScheduleGroup(StartDate, EndDate, null);

        await _groups.ReceivedWithAnyArgs(1).AddToGroupAsync(default!, default!, default);
    }

    [Test]
    public async Task JoinScheduleGroup_WithoutUserIdClaim_IsRefused()
    {
        await Hub(null).JoinScheduleGroup(StartDate, EndDate, _hiddenToken.ToString());

        await _groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Test]
    public async Task SetSelectedGroup_OfAHiddenGroup_LeavesTheFilterUnchanged()
    {
        await Hub(Guid.NewGuid().ToString()).SetSelectedGroup(_hiddenGroupId.ToString());

        await _tracker.DidNotReceiveWithAnyArgs().SetSelectedGroupAsync(default!, default, default);
    }

    [Test]
    public async Task SetSelectedGroup_OfAVisibleGroup_SetsTheFilter()
    {
        await Hub(Guid.NewGuid().ToString()).SetSelectedGroup(_visibleGroupId.ToString());

        await _tracker.Received(1).SetSelectedGroupAsync(ConnectionId, _visibleGroupId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetSelectedGroup_WithoutUserIdClaim_IsRefused()
    {
        await Hub(null).SetSelectedGroup(_visibleGroupId.ToString());

        await _tracker.DidNotReceiveWithAnyArgs().SetSelectedGroupAsync(default!, default, default);
    }

    [Test]
    public async Task JoinScheduleGroup_OfARestrictedUser_CachesTheVisibleClientsBeforeRegistering()
    {
        await Hub(Guid.NewGuid().ToString()).JoinScheduleGroup(StartDate, EndDate, null);

        Received.InOrder(() =>
        {
            _tracker.SetVisibilityAsync(ConnectionId, Arg.Is<ConnectionVisibility>(v =>
                !v.IsUnrestricted && v.AllowsClient(_visibleClientId) && !v.AllowsClient(_hiddenClientId)
                && v.AllowsGroup(_visibleGroupId) && !v.AllowsGroup(_hiddenGroupId)), Arg.Any<CancellationToken>());
            _tracker.RegisterConnectionAsync(ConnectionId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), null, Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task JoinScheduleGroup_OfAnUnrestrictedUser_CachesUnrestrictedVisibility()
    {
        await Hub(Guid.NewGuid().ToString(), unrestricted: true).JoinScheduleGroup(StartDate, EndDate, null);

        await _tracker.Received(1).SetVisibilityAsync(
            ConnectionId, Arg.Is<ConnectionVisibility>(v => v.IsUnrestricted), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetSelectedGroup_RefreshesTheCachedVisibility()
    {
        await Hub(Guid.NewGuid().ToString()).SetSelectedGroup(_visibleGroupId.ToString());

        await _tracker.Received(1).SetVisibilityAsync(
            ConnectionId, Arg.Is<ConnectionVisibility>(v => v.AllowsClient(_visibleClientId)), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task JoinClientGroup_OfAHiddenClient_DoesNotJoin()
    {
        await Hub(Guid.NewGuid().ToString()).JoinClientGroup(_hiddenClientId.ToString());

        await _groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Test]
    public async Task JoinClientGroup_OfAVisibleClient_Joins()
    {
        await Hub(Guid.NewGuid().ToString()).JoinClientGroup(_visibleClientId.ToString());

        await _groups.Received(1).AddToGroupAsync(
            ConnectionId, WorkNotificationHub.GetClientGroupName(_visibleClientId.ToString()), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task JoinClientGroups_JoinsOnlyTheVisibleClients()
    {
        await Hub(Guid.NewGuid().ToString()).JoinClientGroups([_visibleClientId.ToString(), _hiddenClientId.ToString()]);

        await _groups.Received(1).AddToGroupAsync(
            ConnectionId, WorkNotificationHub.GetClientGroupName(_visibleClientId.ToString()), Arg.Any<CancellationToken>());
        await _groups.DidNotReceive().AddToGroupAsync(
            ConnectionId, WorkNotificationHub.GetClientGroupName(_hiddenClientId.ToString()), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task JoinClientGroup_WithoutUserIdClaim_IsRefused()
    {
        await Hub(null).JoinClientGroup(_visibleClientId.ToString());

        await _groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }
}