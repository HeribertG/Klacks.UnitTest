// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The AutoWizard hub admits every planner since autofill is planning work (owner rule 2026-11-07), and its completion
/// events carry employee names, so JoinJob joins only a job the caller may address (IAutoWizardJobAccessGuard). The check
/// runs as the connection's own user (ExecutionPrincipal from Context.User), because IHttpContextAccessor may be empty
/// inside a hub invocation and an empty caller would count as unrestricted; a connection without a user id joins nothing.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Application.Interfaces.Schedules.AutoWizard;
using Klacks.Api.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Klacks.UnitTest.Infrastructure.Hubs;

[TestFixture]
public class AutoWizardJobHubJoinTests
{
    private const string ConnectionId = "connection-1";

    private IAutoWizardJobAccessGuard _access = null!;
    private IGroupManager _groups = null!;

    [SetUp]
    public void SetUp()
    {
        _access = Substitute.For<IAutoWizardJobAccessGuard>();
        _groups = Substitute.For<IGroupManager>();
    }

    private AutoWizardJobHub Hub(string? userId)
    {
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns(ConnectionId);
        var claims = userId is null ? Array.Empty<Claim>() : [new Claim(ClaimTypes.NameIdentifier, userId)];
        context.User.Returns(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));

        return new AutoWizardJobHub(_access) { Context = context, Groups = _groups };
    }

    [Test]
    public async Task JoinJob_JoinsAJobTheCallerMayAddress_CheckedAsTheConnectionsUser()
    {
        var jobId = Guid.NewGuid();
        var userId = Guid.NewGuid().ToString();
        string? checkedAs = null;
        _access.CanAccessAsync(jobId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                checkedAs = ExecutionPrincipal.CurrentUserId;
                return Task.FromResult(true);
            });

        await Hub(userId).JoinJob(jobId);

        checkedAs.ShouldBe(userId);
        await _groups.Received(1).AddToGroupAsync(
            ConnectionId, SignalRConstants.AutoWizardGroups.AutoWizardJob(jobId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task JoinJob_IgnoresAJobTheCallerMayNotAddress()
    {
        var jobId = Guid.NewGuid();
        _access.CanAccessAsync(jobId, Arg.Any<CancellationToken>()).Returns(false);

        await Hub(Guid.NewGuid().ToString()).JoinJob(jobId);

        await _groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Test]
    public async Task JoinJob_RefusesAConnectionWithoutAUserId()
    {
        _access.CanAccessAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        await Hub(null).JoinJob(Guid.NewGuid());

        await _access.DidNotReceiveWithAnyArgs().CanAccessAsync(default, default);
        await _groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }
}
