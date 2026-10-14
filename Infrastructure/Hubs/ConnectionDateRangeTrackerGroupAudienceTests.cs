// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Infrastructure.Hubs;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Hubs;

/// <summary>
/// The audience of news about a whole group (an autofill run): every open schedule whose user may see the group, in any
/// scenario and any date range. A group-less subject reaches unrestricted users only, and a connection whose visibility
/// was never resolved reaches nobody.
/// </summary>
[TestFixture]
public sealed class ConnectionDateRangeTrackerGroupAudienceTests
{
    private const string AdminConnection = "conn-admin";
    private const string OwnGroupConnection = "conn-own";
    private const string ForeignGroupConnection = "conn-foreign";
    private const string UnresolvedConnection = "conn-unresolved";
    private static readonly Guid OwnGroup = Guid.NewGuid();
    private static readonly Guid ForeignGroup = Guid.NewGuid();
    private static readonly DateOnly July1 = new(2026, 7, 1);
    private static readonly DateOnly July31 = new(2026, 7, 31);
    private static readonly DateOnly March1 = new(2027, 3, 1);
    private static readonly DateOnly March31 = new(2027, 3, 31);

    private ConnectionDateRangeTracker _sut = null!;

    [SetUp]
    public async Task SetUp()
    {
        _sut = new ConnectionDateRangeTracker();

        await _sut.RegisterConnectionAsync(AdminConnection, July1, July31, analyseToken: null);
        await _sut.SetVisibilityAsync(AdminConnection, ConnectionVisibility.Unrestricted);

        await _sut.RegisterConnectionAsync(OwnGroupConnection, March1, March31, analyseToken: Guid.NewGuid());
        await _sut.SetSelectedGroupAsync(OwnGroupConnection, ForeignGroup);
        await _sut.SetVisibilityAsync(OwnGroupConnection, ConnectionVisibility.Restricted([], [OwnGroup]));

        await _sut.RegisterConnectionAsync(ForeignGroupConnection, July1, July31, analyseToken: null);
        await _sut.SetVisibilityAsync(ForeignGroupConnection, ConnectionVisibility.Restricted([], [ForeignGroup]));

        await _sut.RegisterConnectionAsync(UnresolvedConnection, July1, July31, analyseToken: null);
    }

    [Test]
    public async Task GroupNews_ReachesEveryConnectionThatMaySeeTheGroup_WhateverItsScenarioRangeOrFilter()
    {
        var audience = await _sut.GetConnectionsForGroupAudienceAsync(OwnGroup);

        audience.Select(connection => connection.ConnectionId)
            .ShouldBe(new[] { AdminConnection, OwnGroupConnection }, ignoreOrder: true);
    }

    [Test]
    public async Task GroupLessNews_ReachesUnrestrictedConnectionsOnly()
    {
        var audience = await _sut.GetConnectionsForGroupAudienceAsync(groupId: null);

        audience.Select(connection => connection.ConnectionId).ShouldBe(new[] { AdminConnection });
    }

    [Test]
    public async Task ConnectionWithUnresolvedVisibility_IsNeverInTheAudience()
    {
        var audiences = new[]
        {
            await _sut.GetConnectionsForGroupAudienceAsync(OwnGroup),
            await _sut.GetConnectionsForGroupAudienceAsync(ForeignGroup),
            await _sut.GetConnectionsForGroupAudienceAsync(groupId: null),
        };

        audiences.SelectMany(audience => audience).ShouldNotContain(connection => connection.ConnectionId == UnresolvedConnection);
    }
}
