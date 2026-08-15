// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Infrastructure.Hubs;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Hubs;

/// <summary>
/// The tracker decides who receives a schedule notification. Two mistakes here are invisible in
/// production: an off-by-one at the range boundary silently drops the first and last day of every
/// open view, and a lost group selection moves a connection into the unfiltered bucket, where it is
/// shown collisions of clients outside its group. Both are pinned here.
/// </summary>
[TestFixture]
public sealed class ConnectionDateRangeTrackerTests
{
    private const string Connection = "connection-a";
    private const string OtherConnection = "connection-b";
    private static readonly DateOnly July1 = new(2026, 7, 1);
    private static readonly DateOnly July15 = new(2026, 7, 15);
    private static readonly DateOnly July31 = new(2026, 7, 31);
    private static readonly DateOnly June30 = new(2026, 6, 30);
    private static readonly DateOnly August1 = new(2026, 8, 1);

    private ConnectionDateRangeTracker _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _sut = new ConnectionDateRangeTracker();
    }

    [Test]
    public async Task SingleDayQuery_HitsTheFirstDayOfTheRange()
    {
        await _sut.RegisterConnectionAsync(Connection, July1, July31, null);

        var matches = await _sut.GetConnectionsForDateRangeAsync(July1, July1, null);

        matches.Select(m => m.ConnectionId).ShouldBe([Connection]);
    }

    [Test]
    public async Task SingleDayQuery_HitsTheLastDayOfTheRange()
    {
        await _sut.RegisterConnectionAsync(Connection, July1, July31, null);

        var matches = await _sut.GetConnectionsForDateRangeAsync(July31, July31, null);

        matches.Select(m => m.ConnectionId).ShouldBe([Connection]);
    }

    [Test]
    public async Task SingleDayQuery_HitsADayInsideTheRange()
    {
        await _sut.RegisterConnectionAsync(Connection, July1, July31, null);

        var matches = await _sut.GetConnectionsForDateRangeAsync(July15, July15, null);

        matches.Select(m => m.ConnectionId).ShouldBe([Connection]);
    }

    [Test]
    public async Task SingleDayQuery_MissesTheDayBeforeAndAfterTheRange()
    {
        await _sut.RegisterConnectionAsync(Connection, July1, July31, null);

        (await _sut.GetConnectionsForDateRangeAsync(June30, June30, null)).ShouldBeEmpty();
        (await _sut.GetConnectionsForDateRangeAsync(August1, August1, null)).ShouldBeEmpty();
    }

    [Test]
    public async Task AnalyseTokenIsAnExactPartitionKey_NotARange()
    {
        var scenario = Guid.NewGuid();
        await _sut.RegisterConnectionAsync(Connection, July1, July31, scenario);

        (await _sut.GetConnectionsForDateRangeAsync(July15, July15, null)).ShouldBeEmpty();
        (await _sut.GetConnectionsForDateRangeAsync(July15, July15, Guid.NewGuid())).ShouldBeEmpty();
        (await _sut.GetConnectionsForDateRangeAsync(July15, July15, scenario)).Count.ShouldBe(1);
    }

    [Test]
    public async Task ExcludeConnectionId_LeavesTheSenderOut()
    {
        await _sut.RegisterConnectionAsync(Connection, July1, July31, null);
        await _sut.RegisterConnectionAsync(OtherConnection, July1, July31, null);

        var matches = await _sut.GetConnectionsForDateRangeAsync(July15, July15, null, Connection);

        matches.Select(m => m.ConnectionId).ShouldBe([OtherConnection]);
    }

    [Test]
    public async Task SetSelectedGroupBeforeRegister_SurvivesTheRegistration()
    {
        var group = Guid.NewGuid();

        await _sut.SetSelectedGroupAsync(Connection, group);
        await _sut.RegisterConnectionAsync(Connection, July1, July31, null);

        var snapshot = await _sut.GetConnectionAsync(Connection);

        snapshot.ShouldNotBeNull();
        snapshot!.Start.ShouldBe(July1);
        snapshot.End.ShouldBe(July31);
        snapshot.SelectedGroupId.ShouldBe(group);
    }

    [Test]
    public async Task RegisterWithANewToken_KeepsAnExistingGroupSelection()
    {
        var group = Guid.NewGuid();
        var scenario = Guid.NewGuid();

        await _sut.RegisterConnectionAsync(Connection, July1, July31, null);
        await _sut.SetSelectedGroupAsync(Connection, group);
        await _sut.RegisterConnectionAsync(Connection, July1, July31, scenario);

        var snapshot = await _sut.GetConnectionAsync(Connection);

        snapshot.ShouldNotBeNull();
        snapshot!.SelectedGroupId.ShouldBe(group);
        snapshot.AnalyseToken.ShouldBe(scenario);
    }

    [Test]
    public async Task UnregisteredConnection_HasNoSnapshotEvenWithAPendingGroupSelection()
    {
        await _sut.SetSelectedGroupAsync(Connection, Guid.NewGuid());

        (await _sut.GetConnectionAsync(Connection)).ShouldBeNull();
    }

    [Test]
    public async Task UnregisteredConnection_IsInvisibleToTheGroupPartitioning()
    {
        await _sut.SetSelectedGroupAsync(Connection, Guid.NewGuid());

        var grouped = await _sut.GetConnectionsGroupedBySelectedGroupAsync(null);

        grouped.Ungrouped.ShouldBeEmpty();
        grouped.ByGroup.ShouldBeEmpty();
    }

    [Test]
    public async Task Unregister_DropsTheGroupSelectionToo()
    {
        await _sut.RegisterConnectionAsync(Connection, July1, July31, null);
        await _sut.SetSelectedGroupAsync(Connection, Guid.NewGuid());

        await _sut.UnregisterConnectionAsync(Connection);
        await _sut.RegisterConnectionAsync(Connection, July1, July31, null);

        var snapshot = await _sut.GetConnectionAsync(Connection);

        snapshot.ShouldNotBeNull();
        snapshot!.SelectedGroupId.ShouldBeNull();
    }

    [Test]
    public async Task GroupPartitioning_SplitsFilteredFromUnfilteredConnections()
    {
        var groupOne = Guid.NewGuid();
        var groupTwo = Guid.NewGuid();

        await _sut.RegisterConnectionAsync("conn-one", July1, July31, null);
        await _sut.SetSelectedGroupAsync("conn-one", groupOne);
        await _sut.RegisterConnectionAsync("conn-two", July1, July31, null);
        await _sut.SetSelectedGroupAsync("conn-two", groupTwo);
        await _sut.RegisterConnectionAsync("conn-all", July1, July31, null);

        var grouped = await _sut.GetConnectionsGroupedBySelectedGroupAsync(null);

        grouped.Ungrouped.Select(c => c.ConnectionId).ShouldBe(["conn-all"]);
        grouped.ByGroup[groupOne].Select(c => c.ConnectionId).ShouldBe(["conn-one"]);
        grouped.ByGroup[groupTwo].Select(c => c.ConnectionId).ShouldBe(["conn-two"]);
    }

    [Test]
    public async Task GetConnectionsForDates_MatchesTheUnionOfTheSingleDateQueries()
    {
        await _sut.RegisterConnectionAsync("conn-july", July1, July31, null);
        await _sut.RegisterConnectionAsync("conn-august", August1, August1, null);

        DateOnly[] dates = [July15, August1];

        var batch = (await _sut.GetConnectionsForDatesAsync(dates, null))
            .Select(c => c.ConnectionId)
            .OrderBy(id => id)
            .ToList();

        var union = new HashSet<string>();
        foreach (var date in dates)
        {
            foreach (var connection in await _sut.GetConnectionsForDateRangeAsync(date, date, null))
            {
                union.Add(connection.ConnectionId);
            }
        }

        batch.ShouldBe(union.OrderBy(id => id).ToList());
        batch.ShouldBe(["conn-august", "conn-july"]);
    }

    [Test]
    public async Task GetConnectionsForDates_ReturnsEachConnectionOnlyOnce()
    {
        await _sut.RegisterConnectionAsync(Connection, July1, July31, null);

        var matches = await _sut.GetConnectionsForDatesAsync([July1, July15, July31], null);

        matches.Count.ShouldBe(1);
    }
}
