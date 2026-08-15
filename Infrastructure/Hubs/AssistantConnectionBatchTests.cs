// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Infrastructure.Hubs;
using Klacks.Api.Infrastructure.Plugins;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Hubs;

/// <summary>
/// Broadcasting a plugin event used to ask the tracker once per connected user. The batch lookup has
/// to answer the same thing in one call, because every one of those lookups becomes a network round
/// trip once the tracker is no longer process-local.
/// </summary>
[TestFixture]
public sealed class AssistantConnectionBatchTests
{
    private const string EventType = "plugin.event";
    private const int UserCount = 20;

    [Test]
    public async Task BatchLookup_AnswersTheSameAsThePerUserLookups()
    {
        var tracker = new AssistantConnectionTracker();
        await tracker.RegisterConnectionAsync("user-a", "conn-a1");
        await tracker.RegisterConnectionAsync("user-a", "conn-a2");
        await tracker.RegisterConnectionAsync("user-b", "conn-b1");

        string[] userIds = ["user-a", "user-b"];

        var batch = await tracker.GetConnectionIdsByUserAsync(userIds);

        foreach (var userId in userIds)
        {
            var single = await tracker.GetConnectionIdsAsync(userId);
            batch[userId].OrderBy(id => id).ShouldBe(single.OrderBy(id => id));
        }

        batch["user-a"].OrderBy(id => id).ShouldBe(["conn-a1", "conn-a2"]);
        batch["user-b"].ShouldBe(["conn-b1"]);
    }

    [Test]
    public async Task BatchLookup_OmitsUsersWithoutConnections()
    {
        var tracker = new AssistantConnectionTracker();
        await tracker.RegisterConnectionAsync("user-a", "conn-a1");

        var batch = await tracker.GetConnectionIdsByUserAsync(["user-a", "user-unknown"]);

        batch.ContainsKey("user-a").ShouldBeTrue();
        batch.ContainsKey("user-unknown").ShouldBeFalse();
    }

    [Test]
    public async Task AssistantBroadcast_ResolvesTwentyUsersInASingleBatchCall()
    {
        var tracker = Substitute.For<IAssistantConnectionTracker>();
        var userIds = ConnectedUserIds();
        tracker.GetConnectedUserIdsAsync(Arg.Any<CancellationToken>()).Returns(userIds);
        tracker.GetConnectionIdsByUserAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(ConnectionsByUser(userIds));

        var hubContext = AssistantHubContext(out var client);
        var sut = new AssistantNotificationService(hubContext, tracker, NullLogger<AssistantNotificationService>.Instance);

        await sut.BroadcastPluginEventAsync(EventType, new object());

        await tracker.Received(1).GetConnectionIdsByUserAsync(
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
        await tracker.DidNotReceive().GetConnectionIdsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await client.Received(UserCount).PluginEvent(EventType, Arg.Any<object>());
    }

    [Test]
    public async Task PluginEventBusBroadcast_ResolvesTwentyUsersInASingleBatchCall()
    {
        var tracker = Substitute.For<IAssistantConnectionTracker>();
        var userIds = ConnectedUserIds();
        tracker.GetConnectedUserIdsAsync(Arg.Any<CancellationToken>()).Returns(userIds);
        tracker.GetConnectionIdsByUserAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(ConnectionsByUser(userIds));

        var hubContext = AssistantHubContext(out var client);
        var sut = new PluginEventBus(hubContext, tracker);

        await sut.BroadcastAsync(EventType, new object());

        await tracker.Received(1).GetConnectionIdsByUserAsync(
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
        await tracker.DidNotReceive().GetConnectionIdsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await client.Received(UserCount).PluginEvent(EventType, Arg.Any<object>());
    }

    private static IReadOnlyList<string> ConnectedUserIds()
        => Enumerable.Range(0, UserCount).Select(index => $"user-{index}").ToList();

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ConnectionsByUser(IReadOnlyList<string> userIds)
        => userIds.ToDictionary(userId => userId, userId => (IReadOnlyList<string>)new[] { $"conn-{userId}" });

    private static IHubContext<AssistantNotificationHub, IAssistantClient> AssistantHubContext(out IAssistantClient client)
    {
        client = Substitute.For<IAssistantClient>();
        var clients = Substitute.For<IHubClients<IAssistantClient>>();
        clients.Clients(Arg.Any<IReadOnlyList<string>>()).Returns(client);

        var hubContext = Substitute.For<IHubContext<AssistantNotificationHub, IAssistantClient>>();
        hubContext.Clients.Returns(clients);
        return hubContext;
    }
}
